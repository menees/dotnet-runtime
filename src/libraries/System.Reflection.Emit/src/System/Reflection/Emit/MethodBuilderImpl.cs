﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;

namespace System.Reflection.Emit
{
    internal sealed class MethodBuilderImpl : MethodBuilder
    {
        private Type _returnType;
        private Type[]? _parameterTypes;
        private readonly ModuleBuilderImpl _module;
        private readonly string _name;
        private readonly CallingConventions _callingConventions;
        private readonly TypeBuilderImpl _declaringType;
        private MethodAttributes _attributes;
        private MethodImplAttributes _methodImplFlags;
        private GenericTypeParameterBuilderImpl[]? _typeParameters;
        private ILGeneratorImpl? _ilGenerator;
        private bool _initLocals;

        internal DllImportData? _dllImportData;
        internal List<CustomAttributeWrapper>? _customAttributes;
        internal ParameterBuilderImpl[]? _parameters;
        internal MethodDefinitionHandle _handle;

        internal MethodBuilderImpl(string name, MethodAttributes attributes, CallingConventions callingConventions, Type? returnType,
            Type[]? parameterTypes, ModuleBuilderImpl module, TypeBuilderImpl declaringType)
        {
            _module = module;
            _returnType = returnType ?? _module.GetTypeFromCoreAssembly(CoreTypeId.Void);
            _name = name;
            _attributes = attributes;
            _callingConventions = callingConventions;
            _declaringType = declaringType;

            if (parameterTypes != null)
            {
                _parameterTypes = new Type[parameterTypes.Length];
                _parameters = new ParameterBuilderImpl[parameterTypes.Length + 1]; // parameter 0 reserved for return type
                for (int i = 0; i < parameterTypes.Length; i++)
                {
                    ArgumentNullException.ThrowIfNull(_parameterTypes[i] = parameterTypes[i], nameof(parameterTypes));
                }
            }

            _methodImplFlags = MethodImplAttributes.IL;
            _initLocals = true;
        }

        internal int ParameterCount => _parameterTypes == null? 0 : _parameterTypes.Length;

        internal ILGeneratorImpl? ILGeneratorImpl => _ilGenerator;

        internal BlobBuilder GetMethodSignatureBlob() => MetadataSignatureHelper.MethodSignatureEncoder(_module,
            _parameterTypes, ReturnType, GetSignatureConvention(_callingConventions), GetGenericArguments().Length, !IsStatic);

        internal static SignatureCallingConvention GetSignatureConvention(CallingConventions callingConventions)
        {
            // TODO: find out and handle other SignatureCallingConvention scenarios
            SignatureCallingConvention convention = SignatureCallingConvention.Default;
            if ((callingConventions & CallingConventions.HasThis) != 0 ||
                (callingConventions & CallingConventions.ExplicitThis) != 0)
            {
                convention |= SignatureCallingConvention.ThisCall;
            }

            if ((callingConventions & CallingConventions.VarArgs) != 0)
            {
                convention |= SignatureCallingConvention.VarArgs;
            }

            return convention;
        }
        protected override bool InitLocalsCore
        {
            get { ThrowIfGeneric(); return _initLocals; }
            set { ThrowIfGeneric(); _initLocals = value; }
        }

        private void ThrowIfGeneric() { if (IsGenericMethod && !IsGenericMethodDefinition) throw new InvalidOperationException(); }

        protected override GenericTypeParameterBuilder[] DefineGenericParametersCore(params string[] names)
        {
            if (_typeParameters != null)
                throw new InvalidOperationException(SR.InvalidOperation_GenericParametersAlreadySet);

            var typeParameters = new GenericTypeParameterBuilderImpl[names.Length];
            for (int i = 0; i < names.Length; i++)
            {
                string name = names[i];
                ArgumentNullException.ThrowIfNull(names, nameof(names));
                typeParameters[i] = new GenericTypeParameterBuilderImpl(name, i, this);
            }

            return _typeParameters = typeParameters;
        }

        protected override ParameterBuilder DefineParameterCore(int position, ParameterAttributes attributes, string? strParamName)
        {
            if (position > 0 && (_parameterTypes == null || position > _parameterTypes.Length))
                throw new ArgumentOutOfRangeException(SR.ArgumentOutOfRange_ParamSequence);

            _parameters ??= new ParameterBuilderImpl[1];

            attributes &= ~ParameterAttributes.ReservedMask;
            ParameterBuilderImpl parameter = new ParameterBuilderImpl(this, position, attributes, strParamName);
            _parameters[position] = parameter;
            return parameter;
        }

        protected override ILGenerator GetILGeneratorCore(int size)
        {
            if (IsGenericMethod && !IsGenericMethodDefinition)
            {
                throw new InvalidOperationException();
            }

            if ((_methodImplFlags & MethodImplAttributes.CodeTypeMask) != MethodImplAttributes.IL ||
                (_methodImplFlags & MethodImplAttributes.Unmanaged) != 0 ||
                (_attributes & MethodAttributes.PinvokeImpl) != 0)
            {
                throw new InvalidOperationException(SR.InvalidOperation_ShouldNotHaveMethodBody);
            }

            if ((_attributes & MethodAttributes.Abstract) != 0)
            {
                throw new InvalidOperationException(SR.InvalidOperation_ShouldNotHaveMethodBody);
            }

            return _ilGenerator ??= new ILGeneratorImpl(this, size);
        }

        protected override void SetCustomAttributeCore(ConstructorInfo con, ReadOnlySpan<byte> binaryAttribute)
        {
            // Handle pseudo custom attributes
            switch (con.ReflectedType!.FullName)
            {
                case "System.Runtime.CompilerServices.MethodImplAttribute":
                    int implValue = BinaryPrimitives.ReadUInt16LittleEndian(binaryAttribute.Slice(2));
                    _methodImplFlags |= (MethodImplAttributes)implValue;
                    return;
                case "System.Runtime.InteropServices.DllImportAttribute":
                    {
                        _dllImportData = DllImportData.CreateDllImportData(CustomAttributeInfo.DecodeCustomAttribute(con, binaryAttribute), out var preserveSig);
                        _attributes |= MethodAttributes.PinvokeImpl;
                        if (preserveSig)
                        {
                            _methodImplFlags |= MethodImplAttributes.PreserveSig;
                        }
                    }
                    return;
                case "System.Runtime.InteropServices.PreserveSigAttribute":
                    _methodImplFlags |= MethodImplAttributes.PreserveSig;
                    return;
                case "System.Runtime.CompilerServices.SpecialNameAttribute":
                    _attributes |= MethodAttributes.SpecialName;
                    return;
                case "System.Security.SuppressUnmanagedCodeSecurityAttribute":
                    _attributes |= MethodAttributes.HasSecurity;
                    break;
            }

            _customAttributes ??= new List<CustomAttributeWrapper>();
            _customAttributes.Add(new CustomAttributeWrapper(con, binaryAttribute));
        }

        protected override void SetImplementationFlagsCore(MethodImplAttributes attributes)
        {
            _methodImplFlags = attributes;
        }
        protected override void SetSignatureCore(Type? returnType, Type[]? returnTypeRequiredCustomModifiers, Type[]? returnTypeOptionalCustomModifiers, Type[]? parameterTypes,
            Type[][]? parameterTypeRequiredCustomModifiers, Type[][]? parameterTypeOptionalCustomModifiers)
        {
            if (returnType != null)
            {
                _returnType = returnType;
            }

            if (parameterTypes != null)
            {
                _parameterTypes = new Type[parameterTypes.Length];
                _parameters = new ParameterBuilderImpl[parameterTypes.Length + 1]; // parameter 0 reserved for return type
                for (int i = 0; i < parameterTypes.Length; i++)
                {
                    ArgumentNullException.ThrowIfNull(_parameterTypes[i] = parameterTypes[i], nameof(parameterTypes));
                }
            }
            // TODO: Add support for other parameters: returnTypeRequiredCustomModifiers, returnTypeOptionalCustomModifiers, parameterTypeRequiredCustomModifiers and parameterTypeOptionalCustomModifiers
        }
        public override string Name => _name;
        public override MethodAttributes Attributes => _attributes;
        public override CallingConventions CallingConvention => _callingConventions;
        public override TypeBuilder DeclaringType => _declaringType;
        public override Module Module => _module;
        public override bool ContainsGenericParameters => throw new NotSupportedException();
        public override bool IsGenericMethod => _typeParameters != null;
        public override bool IsGenericMethodDefinition => _typeParameters != null;
        public override bool IsSecurityCritical => true;
        public override bool IsSecuritySafeCritical => false;
        public override bool IsSecurityTransparent => false;
        public override int MetadataToken => _handle == default ? 0 : MetadataTokens.GetToken(_handle);
        public override RuntimeMethodHandle MethodHandle => throw new NotSupportedException(SR.NotSupported_DynamicModule);
        public override Type? ReflectedType { get => throw new NotImplementedException(); }
        public override ParameterInfo ReturnParameter { get => throw new NotImplementedException(); }
        public override Type ReturnType => _returnType;
        public override ICustomAttributeProvider ReturnTypeCustomAttributes { get => throw new NotImplementedException(); }

        public override MethodInfo GetBaseDefinition() => this;

        public override object[] GetCustomAttributes(bool inherit) => throw new NotSupportedException(SR.NotSupported_DynamicModule);

        public override object[] GetCustomAttributes(Type attributeType, bool inherit) => throw new NotSupportedException(SR.NotSupported_DynamicModule);

        public override Type[] GetGenericArguments() => _typeParameters ?? Type.EmptyTypes;

        public override MethodInfo GetGenericMethodDefinition() => !IsGenericMethod ? throw new InvalidOperationException() : this;

        public override int GetHashCode()
            => throw new NotImplementedException();

        public override MethodImplAttributes GetMethodImplementationFlags()
            => _methodImplFlags;

        public override ParameterInfo[] GetParameters() => throw new NotImplementedException();

        public override object Invoke(object? obj, BindingFlags invokeAttr, Binder? binder, object?[]? parameters, CultureInfo? culture)
             => throw new NotSupportedException(SR.NotSupported_DynamicModule);

        public override bool IsDefined(Type attributeType, bool inherit) => throw new NotSupportedException(SR.NotSupported_DynamicModule);

        [RequiresDynamicCode("The native code for this instantiation might not be available at runtime.")]
        [RequiresUnreferencedCode("If some of the generic arguments are annotated (either with DynamicallyAccessedMembersAttribute, or generic constraints), trimming can't validate that the requirements of those annotations are met.")]
        public override MethodInfo MakeGenericMethod(params Type[] typeArguments)
            => throw new NotImplementedException();
    }
}
