﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Reflection.Emit;

namespace BLToolkit.Emit
{
    /// <summary>
    /// Creates and compiles types instances and methods using expressions, IL code generation
    /// </summary>
    /// <remarks>
    /// Inspired from:
    /// http://abhi.dcmembers.com/blog/2009/03/25/lambda-based-reflection-vs-normal-reflection-vs-direct-call-4/
    /// </remarks>
    public static class FunctionFactory
    {
        #region Delegates

        public delegate object GenericGetter(object target);

        public delegate void GenericSetter(object target, object value);

        #endregion

        #region Nested type: Il

        public static class Il
        {
            public static object CreateInstance(Type type)
            {
                InstantiateObjectHandler instantiateObjectHandler =
                    DynamicMethodCompiler.CreateInstantiateObjectHandler(type);

                return instantiateObjectHandler();
            }

            public static T CreateInstance<T>()
            {
                return (T) CreateInstance(typeof (T));
            }

            public static SetHandler CreateSetHandler(Type type, string property)
            {
                PropertyInfo propertyInfo = type.GetProperty(property);
                return CreateSetHandler(type, propertyInfo);
            }

            public static SetHandler CreateSetHandler(Type type, PropertyInfo propertyInfo)
            {
                SetHandler setHandler = DynamicMethodCompiler.CreateSetHandler(type, propertyInfo);
                return setHandler;
            }

            public static SetHandler CreateSetHandler<T>(string property)
            {
                return CreateSetHandler(typeof (T), property);
            }

            public static GetHandler CreateGetHandler(Type type, PropertyInfo propertyInfo)
            {
                GetHandler getHandler = DynamicMethodCompiler.CreateGetHandler(type, propertyInfo);
                return getHandler;
            }

            public static GetHandler CreateGetHandler<T>(PropertyInfo propertyInfo)
            {
                return CreateGetHandler(typeof (T), propertyInfo);
            }

            public static GetHandler CreateGetHandler<T>(string property)
            {
                PropertyInfo propertyInfo = typeof (T).GetProperty(property);
                return CreateGetHandler<T>(propertyInfo);
            }

            ///
            /// Creates a dynamic setter for the property
            ///
            public static GenericSetter CreateSetMethod(PropertyInfo propertyInfo)
            {
                /*
                * If there's no setter return null
                */
                MethodInfo setMethod = propertyInfo.GetSetMethod();
                if (setMethod == null)
                    return null;

                /*
                * Create the dynamic method
                */
                var arguments = new Type[2];
                arguments[0] = arguments[1] = typeof (object);

                var setter = new DynamicMethod(
                    String.Concat("_Set", propertyInfo.Name, "_"),
                    typeof (void), arguments, propertyInfo.DeclaringType);
                ILGenerator generator = setter.GetILGenerator();
                generator.Emit(OpCodes.Ldarg_0);
                generator.Emit(OpCodes.Castclass, propertyInfo.DeclaringType);
                generator.Emit(OpCodes.Ldarg_1);

                if (propertyInfo.PropertyType.IsClass)
                    generator.Emit(OpCodes.Castclass, propertyInfo.PropertyType);
                else
                    generator.Emit(OpCodes.Unbox_Any, propertyInfo.PropertyType);

                generator.EmitCall(OpCodes.Callvirt, setMethod, null);
                generator.Emit(OpCodes.Ret);

                /*
                * Create the delegate and return it
                */
                return (GenericSetter) setter.CreateDelegate(typeof (GenericSetter));
            }

            ///
            /// Creates a dynamic getter for the property
            ///
            public static GenericGetter CreateGetMethod(PropertyInfo propertyInfo)
            {
                /*
                * If there's no getter return null
                */
                MethodInfo getMethod = propertyInfo.GetGetMethod();
                if (getMethod == null)
                    return null;

                /*
                * Create the dynamic method
                */
                var arguments = new Type[1];
                arguments[0] = typeof (object);

                var getter = new DynamicMethod(
                    String.Concat("_Get", propertyInfo.Name, "_"),
                    typeof (object), arguments, propertyInfo.DeclaringType);

                ILGenerator generator = getter.GetILGenerator();
                generator.DeclareLocal(typeof (object));
                generator.Emit(OpCodes.Ldarg_0);
                generator.Emit(OpCodes.Castclass, propertyInfo.DeclaringType);
                generator.EmitCall(OpCodes.Callvirt, getMethod, null);

                if (!propertyInfo.PropertyType.IsClass)
                    generator.Emit(OpCodes.Box, propertyInfo.PropertyType);

                generator.Emit(OpCodes.Ret);

                /*
                * Create the delegate and return it
                */
                return (GenericGetter) getter.CreateDelegate(typeof (GenericGetter));
            }
        }

        #endregion

        #region Nested type: Lambda

        public static class Lambda
        {
            public static T CreateInstance<T>()
            {
                return InstanceCreator<T>.CreateInstance();
            }

            public static List<T> CreateListInstance<T>()
            {
                return InstanceCreator<T>.CreateListInstance();
            }

            public static Action<T, TValue> BuildSet<T, TValue>(string property)
            {
                string[] props = property.Split('.');
                Type type = typeof (T);
                ParameterExpression arg = Expression.Parameter(type, "x");
                ParameterExpression valArg = Expression.Parameter(typeof (TValue), "val");
                Expression expr = arg;
                foreach (string prop in props.Take(props.Length - 1))
                {
                    // use reflection (not ComponentModel) to mirror LINQ 
                    PropertyInfo pi = type.GetProperty(prop);
                    expr = Expression.Property(expr, pi);
                    type = pi.PropertyType;
                }
                // final property set...
                PropertyInfo finalProp = type.GetProperty(props.Last());
                MethodInfo setter = finalProp.GetSetMethod();
                expr = Expression.Call(expr, setter, valArg);
                return Expression.Lambda<Action<T, TValue>>(expr, arg, valArg).Compile();
            }

            public static Func<T, TValue> BuildGet<T, TValue>(string property)
            {
                string[] props = property.Split('.');
                Type type = typeof (T);
                ParameterExpression arg = Expression.Parameter(type, "x");
                Expression expr = arg;
                foreach (string prop in props)
                {
                    // use reflection (not ComponentModel) to mirror LINQ 
                    PropertyInfo pi = type.GetProperty(prop);
                    expr = Expression.Property(expr, pi);
                    type = pi.PropertyType;
                }
                return Expression.Lambda<Func<T, TValue>>(expr, arg).Compile();
            }

            /// <summary>
            /// Creates a compiled delegate function for the specified type and method name
            /// </summary>
            /// <typeparam name="TFunc">Delegate Func to create</typeparam>
            /// <param name="obj">Constant to get method from</param>
            /// <param name="methodName">Method to examine</param>
            /// <returns>Delegate function of the specified methodname</returns>
            public static TFunc CreateFunc<TFunc>(object obj, string methodName)
            {
                var args = new List<ParameterExpression>();

                Type targetType = obj.GetType();
                MethodInfo minfo = targetType.GetMethod(methodName,
                                                        BindingFlags.Instance | BindingFlags.Public |
                                                        BindingFlags.SetProperty);

                if (minfo != null)
                {
                    ConstantExpression target = Expression.Constant(obj);
                    foreach (ParameterInfo arg in minfo.GetParameters())
                        args.Add(Expression.Parameter(arg.ParameterType, arg.Name));
                    MethodCallExpression methodinvokeExpression = Expression.Call(target, minfo, args.ToArray());
                    Expression<TFunc> lambda = Expression.Lambda<TFunc>(methodinvokeExpression, args.ToArray());

                    //now the following Lambda is created:
                    // (TArg1, TArg2) => obj.MethodName(TArg1, TArg2);

                    return lambda.Compile();
                }
                return default(TFunc);
            }

            /// <summary>
            /// Creates a compiled delegate function using expressions, 
            /// the first Func{TObject,TReturn} parameter must be the constant to be passed in
            /// </summary>
            /// <typeparam name="TFunc">Delegate Func to create</typeparam>
            /// <param name="targetType">Type of constant to pass in to the Func</param>
            /// <param name="methodName">Method to examine</param>
            /// <returns>Delegate function of the specified methodname</returns>
            /// <example>
            /// The function Func{TType,TArg1,TArg2} with a method name of "CallMe" would create the following
            /// lambda:
            /// <code>
            /// (TType, TArg1, TArg2) => TType.CallMe(TArg1, TArg2);
            /// </code>
            /// </example>
            public static TFunc CreateFunc<TFunc>(Type targetType, string methodName)
            {
                var args = new List<ParameterExpression>();
                MethodInfo minfo = targetType.GetMethod(methodName,
                                                        BindingFlags.Instance | BindingFlags.Public |
                                                        BindingFlags.SetProperty);

                if (minfo != null)
                {
                    Type objectType = typeof (TFunc).GetGenericArguments().First();
                    ParameterExpression targetParam = Expression.Parameter(objectType, "a");

                    if (!targetType.IsAssignableFrom(objectType))
                        throw new InvalidCastException(string.Format("{0} cannot be cast to {1}", targetType.Name,
                                                                     objectType.Name));

                    UnaryExpression target = Expression.Convert(targetParam, targetType);
                    foreach (ParameterInfo arg in minfo.GetParameters())
                        args.Add(Expression.Parameter(arg.ParameterType, arg.Name));

                    MethodCallExpression methodinvokeExpression = Expression.Call(target, minfo, args.ToArray());
                    Expression<TFunc> lambda = Expression.Lambda<TFunc>(methodinvokeExpression,
                                                                        new[] {targetParam}.Concat(args));

                    //now the following Lambda is created:
                    // (a, TArg1, TArg2) => a.MethodName(TArg1, TArg2);

                    return lambda.Compile();
                }
                return default(TFunc);
            }

            #region Nested type: InstanceCreator

            private static class InstanceCreator<T>
            {
                public static readonly Func<T> CreateInstance =
                    Expression.Lambda<Func<T>>(Expression.New(typeof (T))).Compile();

                public static readonly Func<List<T>> CreateListInstance =
                Expression.Lambda<Func<List<T>>>(Expression.New(typeof(List<T>))).Compile();
            }

            #endregion
        }

        #endregion

        #region Nested type: Remote

        public static class Remote
        {
            public static T CreateInstance<T>()
            {
                return Activator.CreateInstance<T>();
            }

            public  static object CreateInstance(Type type)
            {
                return Activator.CreateInstance(type);
            }
        }

        #endregion
    }
}