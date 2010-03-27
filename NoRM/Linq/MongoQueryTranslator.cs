﻿using System;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Text;
using Norm.BSON;
using Norm.Configuration;

namespace Norm.Linq
{
    /// <summary>
    /// The mongo query translator.
    /// </summary>
    public class MongoQueryTranslator : ExpressionVisitor
    {
        private int _takeCount = Int32.MaxValue;
        private Expression _expression;
        private bool _collectionSet;
        private string _lastFlyProperty = string.Empty;
        private string _lastOperator = " == ";
        private StringBuilder _sb;
        private StringBuilder _sbIndexed;

        public String PropName
        {
            get;
            set;
        }
        public String TypeName
        {
            get;
            set;
        }

        public String MethodCall
        {
            get;
            set;
        }

        /// <summary>
        /// Gets a value indicating whether IsComplex.
        /// </summary>
        public bool IsComplex { get; private set; }

        /// <summary>
        /// Gets optimized where clause.
        /// </summary>
        public string OptimizedWhere
        {
            get { return _sbIndexed.ToString(); }
        }

        /// <summary>
        /// Gets conditional count.
        /// </summary>
        public int ConditionalCount { get; private set; }

        /// <summary>
        /// Gets FlyWeight.
        /// </summary>
        public Flyweight FlyWeight { get; private set; }

        /// <summary>
        /// How many to skip.
        /// </summary>
        public int Skip { get; set; }


        /// <summary>
        /// How many to take (Int32.MaxValue) by default.
        /// </summary>
        public int Take
        {
            get
            {
                return this._takeCount;
            }
            set
            {
                this._takeCount = value;
            }
        }

        /// <summary>
        /// Gets where expression.
        /// </summary>
        public string WhereExpression
        {
            get { return _sb.ToString(); }
        }

        public bool UseScopedQualifier { get; set; }

        /// <summary>
        /// The translate collection name.
        /// </summary>
        /// <param name="exp">The exp.</param>
        /// <returns>The collection name.</returns>
        public string TranslateCollectionName(Expression exp)
        {
            ConstantExpression c = null;
            switch (exp.NodeType)
            {
                case ExpressionType.Constant:
                    c = (ConstantExpression)exp;
                    break;
                case ExpressionType.Call:
                    {
                        var m = (MethodCallExpression)exp;
                        c = m.Arguments[0] as ConstantExpression;
                    }
                    break;
            }

            //var result = string.Empty;

            // the first argument is a Constant - it's the query itself
            var q = c.Value as IQueryable;
            var result = q.ElementType.Name;

            return result;
        }

        /// <summary>
        /// Translates LINQ to MongoDB.
        /// </summary>
        /// <param name="exp">The expression.</param>
        /// <returns>The translate.</returns>
        public string Translate(Expression exp)
        {
            return Translate(exp, true);
        }

        public string Translate(Expression exp, bool useScopedQualifier)
        {
            UseScopedQualifier = useScopedQualifier;
            _sb = new StringBuilder();
            _sbIndexed = new StringBuilder();
            FlyWeight = new Flyweight();
            Visit(exp);
            return _sb.ToString();
        }

        /// <summary>
        /// Visits member access.
        /// </summary>
        /// <param name="m">The expression.</param>
        /// <returns></returns>
        /// <exception cref="NotSupportedException">
        /// </exception>
        protected override Expression VisitMemberAccess(MemberExpression m)
        {
            //if(m.Expression.NodeType == ExpressionType.MemberAccess)
            //{
            //    VisitMemberAccess((MemberExpression)m.Expression);
            //}

            if (m.Expression != null && m.Expression.NodeType == ExpressionType.Parameter)
            {
                var alias = MongoConfiguration.GetPropertyAlias(m.Expression.Type, m.Member.Name);

                if (UseScopedQualifier)
                {
                    _sb.Append("this.");
                }
                _sb.Append(alias);

                _lastFlyProperty = alias;
                return m;
            }

            if (m.Member.DeclaringType == typeof(string))
            {
                switch (m.Member.Name)
                {
                    case "Length":
                        _sb.Append("LEN(");
                        Visit(m.Expression);
                        _sb.Append(")");
                        return m;
                }
            }
            else if (m.Member.DeclaringType == typeof(DateTime) || m.Member.DeclaringType == typeof(DateTimeOffset))
            {
                #region DateTime Magic
                var fullName = m.ToString().Split(new[] { '.' }, StringSplitOptions.RemoveEmptyEntries);

                // this is complex
                IsComplex = true;

                // this is a DateProperty hanging off the property - clip the last 2 elements
                var fixedName = fullName.Skip(1).Take(fullName.Length - 2).ToArray();
                var propName = string.Join(".", fixedName);

                // now we get to do some tricky fun with javascript
                switch (m.Member.Name)
                {
                    case "Day":
                        Visit(m.Expression);
                        _sb.Append(".getDate()");
                        return m;
                    case "Month":
                        Visit(m.Expression);
                        _sb.Append(".getMonth()");
                        return m;
                    case "Year":
                        Visit(m.Expression);
                        _sb.Append(".getFullYear()");
                        return m;
                    case "Hour":
                        Visit(m.Expression);
                        _sb.Append(".getHours()");
                        return m;
                    case "Minute":
                        Visit(m.Expression);
                        _sb.Append(".getMinutes()");
                        return m;
                    case "Second":
                        Visit(m.Expression);
                        _sb.Append(".getSeconds()");
                        return m;
                    case "DayOfWeek":
                        Visit(m.Expression);
                        _sb.Append(".getDay()");
                        return m;
                } 
                #endregion
            }
            else
            {
                var fullName = m.ToString().Split(new[] { '.' }, StringSplitOptions.RemoveEmptyEntries);

                // this supports the "deep graph" name - "Product.Address.City"
                var fixedName = fullName.Skip(1).Take(fullName.Length - 1).ToArray();

                String result = "";
                var constant = m.Expression as ConstantExpression;
                if (constant != null)
                {
                    var fi = (FieldInfo) m.Member;
                    var val= fi.GetValue(constant.Value);
                    if (val is String)
                    {
                        result = String.Format("\"{0}\"", val);
                    }
                    else
                    {
                        result = val.ToString();
                    }
                }
                else
                {
                    var expressionRootType = GetParameterExpression((MemberExpression)m.Expression);

                    if (expressionRootType != null)
                    {
                        fixedName = GetDeepAlias(expressionRootType.Type, fixedName);
                    }

                    result = string.Join(".", fixedName);
                    //sb.Append("this." + result);
                    if (UseScopedQualifier)
                    {
                        _sb.Append("this.");
                    }
                }
                _sb.Append(result);

                _lastFlyProperty = result;
                return m;
            }

            // if this is a property NOT on the object...
            throw new NotSupportedException(string.Format("The member '{0}' is not supported", m.Member.Name));
        }

        /// <summary>
        /// Visits a binary expression.
        /// </summary>
        /// <param name="b">The expression.</param>
        /// <returns></returns>
        /// <exception cref="NotSupportedException">
        /// </exception>
        protected override Expression VisitBinary(BinaryExpression b)
        {
            ConditionalCount++;
            _sb.Append("(");
            Visit(b.Left);
            switch (b.NodeType)
            {
                case ExpressionType.And:
                case ExpressionType.AndAlso:
                    _sb.Append(" && ");
                    break;

                case ExpressionType.Or:
                case ExpressionType.OrElse:
                    IsComplex = true;
                    _sb.Append(" || ");
                    break;
                case ExpressionType.Equal:
                    _lastOperator = " === ";//Should this be '===' instead? a la 'Javascript: The good parts'
                    _sb.Append(_lastOperator);
                    break;
                case ExpressionType.NotEqual:
                    _lastOperator = " <> ";
                    _sb.Append(_lastOperator);
                    break;
                case ExpressionType.LessThan:
                    _lastOperator = " < ";
                    _sb.Append(_lastOperator);
                    break;
                case ExpressionType.LessThanOrEqual:
                    _lastOperator = " <= ";
                    _sb.Append(_lastOperator);
                    break;
                case ExpressionType.GreaterThan:
                    _lastOperator = " > ";
                    _sb.Append(_lastOperator);
                    break;
                case ExpressionType.GreaterThanOrEqual:
                    _lastOperator = " >= ";
                    _sb.Append(_lastOperator);
                    break;
                default:
                    throw new NotSupportedException(
                        string.Format("The binary operator '{0}' is not supported", b.NodeType));
            }

            Visit(b.Right);
            _sb.Append(")");
            return b;
        }

        /// <summary>
        /// Visits a constant.
        /// </summary>
        /// <param name="c">The expression.</param>
        /// <returns></returns>
        /// <exception cref="NotSupportedException">
        /// </exception>
        protected override Expression VisitConstant(ConstantExpression c)
        {
            var q = c.Value as IQueryable;
            if (q != null)
            {
                // set the collection name
                this.TypeName = q.ElementType.Name;

                // this is our Query wrapper - see if it has an expression
                var qry = (IMongoQuery)c.Value;
                var innerExpression = qry.GetExpression();
                if (innerExpression.NodeType == ExpressionType.Call)
                {
                    VisitMethodCall(innerExpression as MethodCallExpression);
                }
            }
            else if (c.Value == null)
            {
                _sb.Append("NULL");
            }
            else
            {
                switch (Type.GetTypeCode(c.Value.GetType()))
                {
                    case TypeCode.Boolean:
                        _sb.Append(((bool)c.Value) ? 1 : 0);
                        SetFlyValue(((bool)c.Value) ? 1 : 0);
                        break;
                    case TypeCode.DateTime:
                        var val = "new Date('" + c.Value + "')";
                        _sb.Append(val);
                        SetFlyValue(c.Value);
                        break;
                    case TypeCode.String:
                        var sval = "'" + c.Value + "'";
                        _sb.Append(sval);
                        SetFlyValue(c.Value);
                        break;
                    case TypeCode.Object:
                        if (c.Value is ObjectId)
                        {
                            _sb.AppendFormat("ObjectId('{0}')",c.Value);
                            SetFlyValue(c.Value);
                        }
                        else if (c.Value is Guid)
                        {
                            _sb.AppendFormat("'{0}'", c.Value);
                            SetFlyValue(c.Value);
                        }
                        else
                        {
                            throw new NotSupportedException(string.Format("The constant for '{0}' is not supported", c.Value));
                        }
                        break;
                    default:
                        _sb.Append(c.Value);
                        SetFlyValue(c.Value);
                        break;
                }
            }

            return c;
        }

        /// <summary>
        /// Visits a method call.
        /// </summary>
        /// <param name="m">The expression.</param>
        /// <returns></returns>
        /// <exception cref="NotSupportedException">
        /// </exception>
        protected override Expression VisitMethodCall(MethodCallExpression m)
        {
            if (string.IsNullOrEmpty(this.MethodCall))
            {
                this.MethodCall = m.Method.Name;
            }

            if (m.Method.DeclaringType == typeof(Queryable) && m.Method.Name == "Where")
            {
                var lambda = (LambdaExpression)StripQuotes(m.Arguments[1]);
                Visit(lambda.Body);
                return m;
            }
            if (m.Method.DeclaringType == typeof(string))
            {
                IsComplex = true;
                switch (m.Method.Name)
                {
                    case "StartsWith":
                        _sb.Append("(");
                        Visit(m.Object);
                        _sb.Append(".indexOf(");
                        Visit(m.Arguments[0]);
                        _sb.Append(")===0)");
                        return m;

                    case "Contains":
                        _sb.Append("(");
                        Visit(m.Object);
                        _sb.Append(".indexOf(");
                        Visit(m.Arguments[0]);
                        _sb.Append(")>0)");
                        return m;
                    case "IndexOf":
                        Visit(m.Object);
                        _sb.Append(".indexOf(");
                        Visit(m.Arguments[0]);
                        _sb.Append(")");
                        return m;
                    case "EndsWith":
                        _sb.Append("(");
                        Visit(m.Object);
                        _sb.Append(".match(");
                        Visit(m.Arguments[0]);
                        _sb.Append("+'$')==");
                        Visit(m.Arguments[0]);
                        _sb.Append(")");
                        return m;

                    case "IsNullOrEmpty":
                        _sb.Append("(");
                        Visit(m.Arguments[0]);
                        _sb.Append(" == '' ||  ");
                        Visit(m.Arguments[0]);
                        _sb.Append(" == null  )");
                        return m;
                }
            }
            else if (m.Method.DeclaringType == typeof(DateTime))
            {
                //switch (m.Method.Name)
                //{
                //}
            }
            else if (m.Method.DeclaringType == typeof(Queryable) && IsCallableMethod(m.Method.Name))
            {
                return HandleMethodCall(m);
            }
            else if (m.Method.DeclaringType == typeof(Enumerable))
            {
                // Subquery - Count() or Sum()
                if (IsCallableMethod(m.Method.Name))
                {
                }
            }

            // for now...
            throw new NotSupportedException(string.Format("The method '{0}' is not supported", m.Method.Name));
        }

        /// <summary>
        /// Determines if it's a callable method.
        /// </summary>
        /// <param name="methodName">The method name.</param>
        /// <returns>The is callable method.</returns>
        private static bool IsCallableMethod(string methodName)
        {
            var acceptableMethods = new[]
                                        {
                                            "First",
                                            "Single",
                                            "FirstOrDefault",
                                            "SingleOrDefault",
                                            "Count",
                                            "Sum",
                                            "Average",
                                            "Min",
                                            "Max",
                                            "Any",
                                            "Take",
                                            "Skip",
                                            "Count"
                                        };
            return acceptableMethods.Any(x => x == methodName);
        }

        /// <summary>
        /// Strip quotes.
        /// </summary>
        /// <param name="e">The expression.</param>
        /// <returns></returns>
        private static Expression StripQuotes(Expression e)
        {
            while (e.NodeType == ExpressionType.Quote)
            {
                e = ((UnaryExpression)e).Operand;
            }

            return e;
        }

        /// <summary>
        /// The get parameter expression.
        /// </summary>
        /// <param name="expression">
        /// The expression.
        /// </param>
        /// <returns>
        /// </returns>
        private static ParameterExpression GetParameterExpression(Expression expression)
        {
            var expressionRoot = false;
            Expression parentExpression = expression;

            while (!expressionRoot)
            {
                parentExpression = ((MemberExpression)parentExpression).Expression;
                expressionRoot = parentExpression is ParameterExpression;
            }

            return (ParameterExpression)parentExpression;
        }

        /// <summary>
        /// The get deep alias.
        /// </summary>
        /// <param name="type">The type.</param>
        /// <param name="graph">The graph.</param>
        /// <returns></returns>
        private static string[] GetDeepAlias(Type type, string[] graph)
        {
            var graphParts = new string[graph.Length];
            var typeToQuery = type;

            for (var i = 0; i < graph.Length; i++)
            {
                var prpperty = BSON.TypeHelper.FindProperty(typeToQuery, graph[i]);
                graphParts[i] = MongoConfiguration.GetPropertyAlias(typeToQuery, graph[i]);
                typeToQuery = prpperty.PropertyType;
            }

            return graphParts;
        }

        /// <summary>
        /// The set flyweight value.
        /// </summary>
        /// <param name="value">The value.</param>
        private void SetFlyValue(object value)
        {
            // if the property has already been set, we can't set it again
            // as fly uses Dictionaries. This means to BETWEEN style native queries
            if (FlyWeight.Contains(_lastFlyProperty))
            {
                IsComplex = true;
                return;
            }

            if (_lastOperator != " === ")
            { 
                // Can't do comparisons here unless the type is a double
                // which is a limitation of mongo, apparently
                // and won't work if we're doing date comparisons
                if (value.GetType().IsAssignableFrom(typeof(double)))
                {
                    switch (_lastOperator)
                    {
                        case " > ":
                            FlyWeight[_lastFlyProperty] = Q.GreaterThan((double)value);
                            break;
                        case " < ":
                            FlyWeight[_lastFlyProperty] = Q.LessThan((double)value);
                            break;
                        case " <= ":
                            FlyWeight[_lastFlyProperty] = Q.LessOrEqual((double)value);
                            break;
                        case " >= ":
                            FlyWeight[_lastFlyProperty] = Q.GreaterOrEqual((double)value);
                            break;
                        case " <> ":
                            FlyWeight[_lastFlyProperty] = Q.NotEqual(value);
                            break;
                    }
                }
                else
                {
                    // Can't assign? Push to the $where
                    IsComplex = true;
                }
            }
            else
            {
                FlyWeight[_lastFlyProperty] = value;
            }
        }

        /// <summary>
        /// Handles skip.
        /// </summary>
        /// <param name="exp">The expression.</param>
        private void HandleSkip(Expression exp)
        {
            this.Skip = (int)exp.GetConstantValue();
        }

        /// <summary>
        /// Handles take.
        /// </summary>
        /// <param name="exp">The expression.</param>
        private void HandleTake(Expression exp)
        {
            this.Take = (int)exp.GetConstantValue();
        }

        /// <summary>
        /// The handle method call.
        /// </summary>
        /// <param name="m">The expression.</param>
        /// <returns></returns>
        private Expression HandleMethodCall(MethodCallExpression m)
        {
            switch (m.Method.Name)
            {
                case "Skip":
                    HandleSkip(m.Arguments[1]);
                    return m;
                case "Take":
                    HandleTake(m.Arguments[1]);
                    Visit(m.Arguments[0]);
                    return m;
                default:
                    this.Take = 1;
                    this.MethodCall = m.Method.Name;
                    if (m.Arguments.Count > 1)
                    {
                        var lambda = (LambdaExpression)StripQuotes(m.Arguments[1]);
                        if (lambda != null)
                        {
                            Visit(lambda.Body);
                        }
                        else
                        {
                            Visit(m.Arguments[0]);
                        }
                    }
                    else
                    {
                        Visit(m.Arguments[0]);
                    }
                    break;
            }

            return m;
        }
    }
}