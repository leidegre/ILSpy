using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ICSharpCode.Decompiler;
using ICSharpCode.NRefactory.CSharp;
using Mono.Cecil;
using Mono.Cecil.Cil;
using ClassType = ICSharpCode.NRefactory.TypeSystem.ClassType;

namespace Decompiler
{
	public class AstBuilder
	{
		CompilationUnit astCompileUnit = new CompilationUnit();
		Dictionary<string, NamespaceDeclaration> astNamespaces = new Dictionary<string, NamespaceDeclaration>();
		
		public void GenerateCode(ITextOutput output)
		{
			for (int i = 0; i < 4; i++) {
				if (Options.ReduceAstJumps) {
					//astCompileUnit.AcceptVisitor(new Transforms.Ast.RemoveGotos(), null);
					astCompileUnit.AcceptVisitor(new Transforms.Ast.RemoveDeadLabels(), null);
				}
				if (Options.ReduceAstLoops) {
					//astCompileUnit.AcceptVisitor(new Transforms.Ast.RestoreLoop(), null);
				}
				if (Options.ReduceAstOther) {
					astCompileUnit.AcceptVisitor(new Transforms.Ast.Idioms(), null);
					astCompileUnit.AcceptVisitor(new Transforms.Ast.RemoveEmptyElseBody(), null);
					astCompileUnit.AcceptVisitor(new Transforms.Ast.PushNegation(), null);
				}
			}
			if (Options.ReduceAstOther) {
				astCompileUnit.AcceptVisitor(new Transforms.Ast.SimplifyTypeReferences(), null);
				astCompileUnit.AcceptVisitor(new Transforms.Ast.Idioms(), null);
			}
			if (Options.ReduceAstLoops) {
				//astCompileUnit.AcceptVisitor(new Transforms.Ast.RestoreLoop(), null);
			}
			
			var outputFormatter = new TextOutputFormatter(output);
			var formattingPolicy = new CSharpFormattingPolicy();
			// disable whitespace in front of parentheses:
			formattingPolicy.BeforeMethodCallParentheses = false;
			formattingPolicy.BeforeMethodDeclarationParentheses = false;
			astCompileUnit.AcceptVisitor(new OutputVisitor(outputFormatter, formattingPolicy), null);
		}
		
		public void AddAssembly(AssemblyDefinition assemblyDefinition)
		{
			astCompileUnit.AddChild(
				new UsingDeclaration {
					Import = new SimpleType("System")
				}, CompilationUnit.MemberRole);
			
			foreach(TypeDefinition typeDef in assemblyDefinition.MainModule.Types) {
				// Skip nested types - they will be added by the parent type
				if (typeDef.DeclaringType != null) continue;
				// Skip the <Module> class
				if (typeDef.Name == "<Module>") continue;
				
				AddType(typeDef);
			}
		}
		
		NamespaceDeclaration GetCodeNamespace(string name)
		{
			if (string.IsNullOrEmpty(name)) {
				return null;
			}
			if (astNamespaces.ContainsKey(name)) {
				return astNamespaces[name];
			} else {
				// Create the namespace
				NamespaceDeclaration astNamespace = new NamespaceDeclaration { Name = name };
				astCompileUnit.AddChild(astNamespace, CompilationUnit.MemberRole);
				astNamespaces[name] = astNamespace;
				return astNamespace;
			}
		}
		
		public void AddType(TypeDefinition typeDef)
		{
			TypeDeclaration astType = CreateType(typeDef);
			NamespaceDeclaration astNS = GetCodeNamespace(typeDef.Namespace);
			if (astNS != null) {
				astNS.AddChild(astType, NamespaceDeclaration.MemberRole);
			} else {
				astCompileUnit.AddChild(astType, CompilationUnit.MemberRole);
			}
		}
		
		public TypeDeclaration CreateType(TypeDefinition typeDef)
		{
			TypeDeclaration astType = new TypeDeclaration();
			astType.Modifiers = ConvertModifiers(typeDef);
			astType.Name = typeDef.Name;
			
			if (typeDef.IsEnum) {  // NB: Enum is value type
				astType.ClassType = ClassType.Enum;
			} else if (typeDef.IsValueType) {
				astType.ClassType = ClassType.Struct;
			} else if (typeDef.IsInterface) {
				astType.ClassType = ClassType.Interface;
			} else {
				astType.ClassType = ClassType.Class;
			}
			
			// Nested types
			foreach(TypeDefinition nestedTypeDef in typeDef.NestedTypes) {
				astType.AddChild(CreateType(nestedTypeDef), TypeDeclaration.MemberRole);
			}
			
			// Base type
			if (typeDef.BaseType != null && !typeDef.IsValueType && typeDef.BaseType.FullName != Constants.Object) {
				astType.AddChild(ConvertType(typeDef.BaseType), TypeDeclaration.BaseTypeRole);
			}
			foreach (var i in typeDef.Interfaces)
				astType.AddChild(ConvertType(i), TypeDeclaration.BaseTypeRole);
			
			AddTypeMembers(astType, typeDef);
			
			return astType;
		}
		
		#region Convert Type Reference
		/// <summary>
		/// Converts a type reference.
		/// </summary>
		/// <param name="type">The Cecil type reference that should be converted into
		/// a type system type reference.</param>
		/// <param name="typeAttributes">Attributes associated with the Cecil type reference.
		/// This is used to support the 'dynamic' type.</param>
		public static AstType ConvertType(TypeReference type, ICustomAttributeProvider typeAttributes = null)
		{
			int typeIndex = 0;
			return CreateType(type, typeAttributes, ref typeIndex);
		}
		
		static AstType CreateType(TypeReference type, ICustomAttributeProvider typeAttributes, ref int typeIndex)
		{
			while (type is OptionalModifierType || type is RequiredModifierType) {
				type = ((TypeSpecification)type).ElementType;
			}
			if (type == null) {
				return AstType.Null;
			}
			
			if (type is Mono.Cecil.ByReferenceType) {
				typeIndex++;
				// ignore by reference type (cannot be represented in C#)
				return CreateType((type as Mono.Cecil.ByReferenceType).ElementType, typeAttributes, ref typeIndex);
			} else if (type is Mono.Cecil.PointerType) {
				typeIndex++;
				return CreateType((type as Mono.Cecil.PointerType).ElementType, typeAttributes, ref typeIndex)
					.MakePointerType();
			} else if (type is Mono.Cecil.ArrayType) {
				typeIndex++;
				return CreateType((type as Mono.Cecil.ArrayType).ElementType, typeAttributes, ref typeIndex)
					.MakeArrayType((type as Mono.Cecil.ArrayType).Rank);
			} else if (type is GenericInstanceType) {
				GenericInstanceType gType = (GenericInstanceType)type;
				AstType baseType = CreateType(gType.ElementType, typeAttributes, ref typeIndex);
				foreach (var typeArgument in gType.GenericArguments) {
					typeIndex++;
					baseType.AddChild(CreateType(typeArgument, typeAttributes, ref typeIndex), AstType.Roles.TypeArgument);
				}
				return baseType;
			} else if (type is GenericParameter) {
				return new SimpleType(type.Name);
			} else if (type.IsNested) {
				AstType typeRef = CreateType(type.DeclaringType, typeAttributes, ref typeIndex);
				string namepart = ICSharpCode.NRefactory.TypeSystem.ReflectionHelper.SplitTypeParameterCountFromReflectionName(type.Name);
				return new MemberType { Target = typeRef, MemberName = namepart };
			} else {
				string ns = type.Namespace ?? string.Empty;
				string name = type.Name;
				if (name == null)
					throw new InvalidOperationException("type.Name returned null. Type: " + type.ToString());
				
				if (name == "Object" && ns == "System" && HasDynamicAttribute(typeAttributes, typeIndex)) {
					return new PrimitiveType("dynamic");
				} else {
					name = ICSharpCode.NRefactory.TypeSystem.ReflectionHelper.SplitTypeParameterCountFromReflectionName(name);
					if (ns.Length == 0)
						return new SimpleType(name);
					string[] parts = ns.Split('.');
					AstType nsType = new SimpleType(parts[0]);
					for (int i = 1; i < parts.Length; i++) {
						nsType = new MemberType { Target = nsType, MemberName = parts[i] };
					}
					return new MemberType { Target = nsType, MemberName = name };
				}
			}
		}
		
		const string DynamicAttributeFullName = "System.Runtime.CompilerServices.DynamicAttribute";
		
		static bool HasDynamicAttribute(ICustomAttributeProvider attributeProvider, int typeIndex)
		{
			if (attributeProvider == null || !attributeProvider.HasCustomAttributes)
				return false;
			foreach (CustomAttribute a in attributeProvider.CustomAttributes) {
				if (a.Constructor.DeclaringType.FullName == DynamicAttributeFullName) {
					if (a.ConstructorArguments.Count == 1) {
						CustomAttributeArgument[] values = a.ConstructorArguments[0].Value as CustomAttributeArgument[];
						if (values != null && typeIndex < values.Length && values[typeIndex].Value is bool)
							return (bool)values[typeIndex].Value;
					}
					return true;
				}
			}
			return false;
		}
		#endregion
		
		#region ConvertModifiers
		Modifiers ConvertModifiers(TypeDefinition typeDef)
		{
			return
				(typeDef.IsNestedPrivate            ? Modifiers.Private    : Modifiers.None) |
				(typeDef.IsNestedFamilyAndAssembly  ? Modifiers.Protected  : Modifiers.None) | // TODO: Extended access
				(typeDef.IsNestedAssembly           ? Modifiers.Internal   : Modifiers.None) |
				(typeDef.IsNestedFamily             ? Modifiers.Protected  : Modifiers.None) |
				(typeDef.IsNestedFamilyOrAssembly   ? Modifiers.Protected | Modifiers.Internal : Modifiers.None) |
				(typeDef.IsPublic                   ? Modifiers.Public     : Modifiers.None) |
				(typeDef.IsAbstract                 ? Modifiers.Abstract   : Modifiers.None);
		}
		
		Modifiers ConvertModifiers(FieldDefinition fieldDef)
		{
			return
				(fieldDef.IsPrivate            ? Modifiers.Private    : Modifiers.None) |
				(fieldDef.IsFamilyAndAssembly  ? Modifiers.Protected  : Modifiers.None) | // TODO: Extended access
				(fieldDef.IsAssembly           ? Modifiers.Internal   : Modifiers.None) |
				(fieldDef.IsFamily             ? Modifiers.Protected  : Modifiers.None) |
				(fieldDef.IsFamilyOrAssembly   ? Modifiers.Protected | Modifiers.Internal : Modifiers.None) |
				(fieldDef.IsPublic             ? Modifiers.Public     : Modifiers.None) |
				(fieldDef.IsLiteral            ? Modifiers.Const      : Modifiers.None) |
				(fieldDef.IsStatic             ? Modifiers.Static     : Modifiers.None);
		}
		
		Modifiers ConvertModifiers(MethodDefinition methodDef)
		{
			return
				(methodDef.IsCompilerControlled ? Modifiers.None       : Modifiers.None) |
				(methodDef.IsPrivate            ? Modifiers.Private    : Modifiers.None) |
				(methodDef.IsFamilyAndAssembly  ? Modifiers.Protected  : Modifiers.None) | // TODO: Extended access
				(methodDef.IsAssembly           ? Modifiers.Internal   : Modifiers.None) |
				(methodDef.IsFamily             ? Modifiers.Protected  : Modifiers.None) |
				(methodDef.IsFamilyOrAssembly   ? Modifiers.Protected | Modifiers.Internal : Modifiers.None) |
				(methodDef.IsPublic             ? Modifiers.Public     : Modifiers.None) |
				(methodDef.IsStatic             ? Modifiers.Static     : Modifiers.None) |
				(methodDef.IsVirtual            ? Modifiers.Virtual    : Modifiers.None) |
				(methodDef.IsAbstract           ? Modifiers.Abstract   : Modifiers.None);
		}
		#endregion
		
		void AddTypeMembers(TypeDeclaration astType, TypeDefinition typeDef)
		{
			// Add fields
			foreach(FieldDefinition fieldDef in typeDef.Fields) {
				astType.AddChild(CreateField(fieldDef), TypeDeclaration.MemberRole);
			}
			
			// Add events
			foreach(EventDefinition eventDef in typeDef.Events) {
				astType.AddChild(CreateEvent(eventDef), TypeDeclaration.MemberRole);
			}
			
			// Add properties
			foreach(PropertyDefinition propDef in typeDef.Properties) {
				astType.AddChild(CreateProperty(propDef), TypeDeclaration.MemberRole);
			}
			
			// Add constructors
			foreach(MethodDefinition methodDef in typeDef.Methods) {
				if (!methodDef.IsConstructor) continue;
				
				astType.AddChild(CreateConstructor(methodDef), TypeDeclaration.MemberRole);
			}
			
			// Add methods
			foreach(MethodDefinition methodDef in typeDef.Methods) {
				if (methodDef.IsSpecialName) continue;
				
				astType.AddChild(CreateMethod(methodDef), TypeDeclaration.MemberRole);
			}
		}

		MethodDeclaration CreateMethod(MethodDefinition methodDef)
		{
			MethodDeclaration astMethod = new MethodDeclaration();
			astMethod.Name = methodDef.Name;
			astMethod.ReturnType = ConvertType(methodDef.ReturnType, methodDef.MethodReturnType);
			astMethod.Modifiers = ConvertModifiers(methodDef);
			astMethod.Parameters = MakeParameters(methodDef.Parameters);
			astMethod.Body = AstMethodBodyBuilder.CreateMetodBody(methodDef);
			return astMethod;
		}

		ConstructorDeclaration CreateConstructor(MethodDefinition methodDef)
		{
			ConstructorDeclaration astMethod = new ConstructorDeclaration();
			astMethod.Modifiers = ConvertModifiers(methodDef);
			astMethod.Parameters = MakeParameters(methodDef.Parameters);
			astMethod.Body = AstMethodBodyBuilder.CreateMetodBody(methodDef);
			return astMethod;
		}

		PropertyDeclaration CreateProperty(PropertyDefinition propDef)
		{
			PropertyDeclaration astProp = new PropertyDeclaration();
			astProp.Modifiers = ConvertModifiers(propDef.GetMethod);
			astProp.Name = propDef.Name;
			astProp.ReturnType = ConvertType(propDef.PropertyType, propDef);
			if (propDef.GetMethod != null) {
				astProp.Getter = new Accessor {
					Body = AstMethodBodyBuilder.CreateMetodBody(propDef.GetMethod)
				};
			}
			if (propDef.SetMethod != null) {
				astProp.Setter = new Accessor {
					Body = AstMethodBodyBuilder.CreateMetodBody(propDef.SetMethod)
				};
			}
			return astProp;
		}

		EventDeclaration CreateEvent(EventDefinition eventDef)
		{
			EventDeclaration astEvent = new EventDeclaration();
			astEvent.Name = eventDef.Name;
			astEvent.ReturnType = ConvertType(eventDef.EventType, eventDef);
			astEvent.Modifiers = ConvertModifiers(eventDef.AddMethod);
			return astEvent;
		}

		FieldDeclaration CreateField(FieldDefinition fieldDef)
		{
			FieldDeclaration astField = new FieldDeclaration();
			astField.AddChild(new VariableInitializer(fieldDef.Name), FieldDeclaration.Roles.Variable);
			astField.ReturnType = ConvertType(fieldDef.FieldType, fieldDef);
			astField.Modifiers = ConvertModifiers(fieldDef);
			return astField;
		}
		
		IEnumerable<ParameterDeclaration> MakeParameters(IEnumerable<ParameterDefinition> paramCol)
		{
			foreach(ParameterDefinition paramDef in paramCol) {
				ParameterDeclaration astParam = new ParameterDeclaration();
				astParam.Type = ConvertType(paramDef.ParameterType, paramDef);
				astParam.Name = paramDef.Name;
				
				if (!paramDef.IsIn && paramDef.IsOut) astParam.ParameterModifier = ParameterModifier.Out;
				if (paramDef.IsIn && paramDef.IsOut)  astParam.ParameterModifier = ParameterModifier.Ref;
				// TODO: params, this
				
				yield return astParam;
			}
		}
	}
}