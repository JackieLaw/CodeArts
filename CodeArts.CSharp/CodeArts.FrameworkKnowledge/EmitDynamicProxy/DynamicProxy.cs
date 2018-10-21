﻿using System;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;

namespace CodeArts.FrameworkKnowledge.EmitDynamicProxy
{
    public class DynamicProxy
    {
        private static readonly string[] _ignoreMethodName = new[] { "GetType", "ToString", "GetHashCode", "Equals" };

        public static TInterface CreateProxyOfRealize<TInterface, TImp>(Type interceptorType = null) where TImp : class, new() where TInterface : class
        {
            return Invoke<TInterface, TImp>(false, interceptorType);
        }

        public static TProxyClass CreateProxyOfInherit<TProxyClass>(Type interceptorType = null) where TProxyClass : class, new()
        {
            return Invoke<TProxyClass, TProxyClass>(true, interceptorType);
        }

        private static TInterface Invoke<TInterface, TImp>(bool inheritMode = false, Type interceptorType = null) where TImp : class where TInterface : class
        {
            if (inheritMode)
                return CreateProxyOfInherit(typeof(TImp), interceptorType) as TInterface;
            else
                return CreateProxyOfRealize(typeof(TInterface), typeof(TImp), interceptorType) as TInterface;
        }

        public static object CreateProxyOfRealize(Type interfaceType, Type impType, Type interceptorType = null)
        {
            string nameOfAssembly = impType.Name + "ProxyAssembly";
            string nameOfModule = impType.Name + "ProxyModule";
            string nameOfType = impType.Name + "Proxy";

            AssemblyBuilder assembly = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName(nameOfAssembly), AssemblyBuilderAccess.RunAndSave);
            ModuleBuilder moduleBuilder = assembly.DefineDynamicModule(nameOfModule, nameOfAssembly + ".dll");
            TypeBuilder typeBuilder = moduleBuilder.DefineType(nameOfType, TypeAttributes.Public, null, new[] { interfaceType });
            MethodAttributes methodAttributes = MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.NewSlot | MethodAttributes.Virtual | MethodAttributes.Final;

            var instance = Invoke(impType, typeBuilder, methodAttributes, interceptorType);
            assembly.Save(nameOfAssembly + ".dll");
            return instance;
        }

        public static object CreateProxyOfInherit(Type impType, Type interceptorType = null)
        {
            string nameOfAssembly = impType.Name + "ProxyAssembly";
            string nameOfModule = impType.Name + "ProxyModule";
            string nameOfType = impType.Name + "Proxy";

            AssemblyBuilder assembly = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName(nameOfAssembly), AssemblyBuilderAccess.Run);
            ModuleBuilder moduleBuilder = assembly.DefineDynamicModule(nameOfModule);
            TypeBuilder typeBuilder = moduleBuilder.DefineType(nameOfType, TypeAttributes.Public, impType);
            MethodAttributes methodAttributes = MethodAttributes.Public | MethodAttributes.Virtual;

            return Invoke(impType, typeBuilder, methodAttributes, interceptorType);
        }

        private static object Invoke(Type impType, TypeBuilder typeBuilder, MethodAttributes methodAttributes, Type interceptorType = null)
        {
            var interceptorAttributeType = impType.GetCustomAttribute(typeof(InterceptorBaseAttribute))?.GetType();
            var serviceProviderType = typeof(ServiceProvider);

            var constructorBuilder = typeBuilder.DefineConstructor(MethodAttributes.Public, CallingConventions.Standard, null);
            var ilOfCtor = constructorBuilder.GetILGenerator();

            // ---- define fields ----
            FieldBuilder fieldInterceptor = null;

            if (interceptorAttributeType != null)
            {
                fieldInterceptor = typeBuilder.DefineField("fieldInterceptor", interceptorAttributeType, FieldAttributes.Private);
                ilOfCtor.Emit(OpCodes.Ldarg_0);
                ilOfCtor.Emit(OpCodes.Newobj, interceptorAttributeType.GetConstructor(new Type[0]));
                ilOfCtor.Emit(OpCodes.Stfld, fieldInterceptor);
            }

            //initial field of serviceProviderType
            FieldBuilder _serviceProvider = typeBuilder.DefineField("_serviceProvider", serviceProviderType, FieldAttributes.Private);
            ilOfCtor.Emit(OpCodes.Ldarg_0);
            ilOfCtor.Emit(OpCodes.Newobj, serviceProviderType.GetConstructor(new Type[0]));
            ilOfCtor.Emit(OpCodes.Stfld, _serviceProvider);

            //initial field of impObj
            FieldBuilder _serviceImpObj = typeBuilder.DefineField("_serviceImpObj", impType, FieldAttributes.Private);
            //ilOfCtor.Emit(OpCodes.Ldarg_0);
            //ilOfCtor.Emit(OpCodes.Newobj, impType.GetConstructor(new Type[0]));
            //ilOfCtor.Emit(OpCodes.Stfld, _serviceImpObj);

            //local type
            //var typeofImp = ilOfCtor.DeclareLocal(typeof(Type));
            //ilOfCtor.Emit(OpCodes.Ldarg_0);//this
            //ilOfCtor.Emit(OpCodes.Ldfld, _serviceImpObj);
            //ilOfCtor.Emit(OpCodes.Callvirt, impType.GetMethod("GetType"));
            //ilOfCtor.Emit(OpCodes.Stloc, typeofImp);

            //调用方法对对象赋值
            ilOfCtor.Emit(OpCodes.Ldarg_0);//this
            ilOfCtor.Emit(OpCodes.Ldarg_0);//this
            ilOfCtor.Emit(OpCodes.Ldfld, _serviceProvider);
            ilOfCtor.Emit(OpCodes.Ldstr, impType.Assembly.FullName);
            ilOfCtor.Emit(OpCodes.Ldstr, impType.FullName);
            ilOfCtor.Emit(OpCodes.Callvirt, serviceProviderType.GetMethod("GetService", new Type[2] { typeof(string), typeof(string) }));

            if (impType.IsValueType)
                ilOfCtor.Emit(OpCodes.Unbox_Any, impType);
            else
                ilOfCtor.Emit(OpCodes.Castclass, impType);

            ilOfCtor.Emit(OpCodes.Stfld, _serviceImpObj);

            ilOfCtor.Emit(OpCodes.Ret);

            // ---- define methods ----

            var methodsOfType = impType.GetMethods(BindingFlags.Public | BindingFlags.Instance);

            foreach (var method in methodsOfType)
            {
                //ignore method
                if (_ignoreMethodName.Contains(method.Name))
                    continue;

                var methodParameterTypes = method.GetParameters().Select(p => p.ParameterType).ToArray();

                var methodBuilder = typeBuilder.DefineMethod(method.Name, methodAttributes, CallingConventions.Standard, method.ReturnType, methodParameterTypes);

                var ilMethod = methodBuilder.GetILGenerator();

                // set local field
                var methodName = ilMethod.DeclareLocal(typeof(string));     //instance of method name
                var parameters = ilMethod.DeclareLocal(typeof(object[]));   //instance of parameters
                var result = ilMethod.DeclareLocal(typeof(object));         //instance of result
                LocalBuilder actionAttributeObj = null;

                //attribute init
                Type actionAttributeType = null;
                if (method.GetCustomAttribute(typeof(ActionBaseAttribute)) != null || impType.GetCustomAttribute(typeof(ActionBaseAttribute)) != null)
                {
                    //method can override class attrubute
                    if (method.GetCustomAttribute(typeof(ActionBaseAttribute)) != null)
                    {
                        actionAttributeType = method.GetCustomAttribute(typeof(ActionBaseAttribute)).GetType();
                    }
                    else if (impType.GetCustomAttribute(typeof(ActionBaseAttribute)) != null)
                    {
                        actionAttributeType = impType.GetCustomAttribute(typeof(ActionBaseAttribute)).GetType();
                    }

                    actionAttributeObj = ilMethod.DeclareLocal(actionAttributeType);
                    ilMethod.Emit(OpCodes.Newobj, actionAttributeType.GetConstructor(new Type[0]));
                    ilMethod.Emit(OpCodes.Stloc, actionAttributeObj);
                }

                //if no attribute
                if (fieldInterceptor != null || actionAttributeObj != null)
                {
                    ilMethod.Emit(OpCodes.Ldstr, method.Name);
                    ilMethod.Emit(OpCodes.Stloc, methodName);

                    ilMethod.Emit(OpCodes.Ldc_I4, methodParameterTypes.Length);
                    ilMethod.Emit(OpCodes.Newarr, typeof(object));
                    ilMethod.Emit(OpCodes.Stloc, parameters);
                    // build the method parameters
                    for (var j = 0; j < methodParameterTypes.Length; j++)
                    {
                        ilMethod.Emit(OpCodes.Ldloc, parameters);
                        ilMethod.Emit(OpCodes.Ldc_I4, j);
                        ilMethod.Emit(OpCodes.Ldarg, j + 1);
                        //box
                        ilMethod.Emit(OpCodes.Box, methodParameterTypes[j]);
                        ilMethod.Emit(OpCodes.Stelem_Ref);
                    }
                }

                //dynamic proxy action before
                if (actionAttributeType != null)
                {
                    //load arguments
                    ilMethod.Emit(OpCodes.Ldloc, actionAttributeObj);
                    ilMethod.Emit(OpCodes.Ldloc, methodName);
                    ilMethod.Emit(OpCodes.Ldloc, parameters);
                    ilMethod.Emit(OpCodes.Call, actionAttributeType.GetMethod("Before"));
                }

                if (interceptorAttributeType != null)
                {
                    //load arguments
                    ilMethod.Emit(OpCodes.Ldarg_0);//this
                    ilMethod.Emit(OpCodes.Ldfld, fieldInterceptor);
                    ilMethod.Emit(OpCodes.Ldarg_0);//this
                    ilMethod.Emit(OpCodes.Ldfld, _serviceImpObj);
                    ilMethod.Emit(OpCodes.Ldloc, methodName);
                    ilMethod.Emit(OpCodes.Ldloc, parameters);
                    // call Invoke() method of Interceptor
                    ilMethod.Emit(OpCodes.Callvirt, interceptorAttributeType.GetMethod("Invoke"));
                }
                else
                {
                    //direct call method
                    if (method.ReturnType == typeof(void) && actionAttributeType == null)
                    {
                        ilMethod.Emit(OpCodes.Ldnull);
                    }

                    ilMethod.Emit(OpCodes.Ldarg_0);//this
                    ilMethod.Emit(OpCodes.Ldfld, _serviceImpObj);
                    for (var j = 0; j < methodParameterTypes.Length; j++)
                    {
                        ilMethod.Emit(OpCodes.Ldarg, j + 1);
                    }
                    ilMethod.Emit(OpCodes.Callvirt, impType.GetMethod(method.Name));
                    //box
                    if (actionAttributeType != null)
                    {
                        if (method.ReturnType != typeof(void))
                            ilMethod.Emit(OpCodes.Box, method.ReturnType);
                        else
                            ilMethod.Emit(OpCodes.Ldnull);
                    }
                }

                //dynamic proxy action after
                if (actionAttributeType != null)
                {
                    ilMethod.Emit(OpCodes.Stloc, result);
                    //load arguments
                    ilMethod.Emit(OpCodes.Ldloc, actionAttributeObj);
                    ilMethod.Emit(OpCodes.Ldloc, methodName);
                    ilMethod.Emit(OpCodes.Ldloc, result);
                    ilMethod.Emit(OpCodes.Call, actionAttributeType.GetMethod("After"));
                }

                // pop the stack if return void
                if (method.ReturnType == typeof(void))
                {
                    ilMethod.Emit(OpCodes.Pop);
                }
                else
                {
                    //unbox,if direct invoke,no box
                    if (fieldInterceptor != null || actionAttributeObj != null)
                    {
                        if (method.ReturnType.IsValueType)
                            ilMethod.Emit(OpCodes.Unbox_Any, method.ReturnType);
                        else
                            ilMethod.Emit(OpCodes.Castclass, method.ReturnType);
                    }
                }
                // complete
                ilMethod.Emit(OpCodes.Ret);
            }


            var t = typeBuilder.CreateType();

            return Activator.CreateInstance(t);
        }
    }
}