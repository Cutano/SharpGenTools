﻿using System;
using System.Collections.Generic;
using System.Text;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;
using SharpGen.Model;
using Microsoft.CodeAnalysis.CSharp;
using System.Linq;

namespace SharpGen.Generator
{
    class ShadowCallbackGenerator : MarshallingCodeGeneratorBase, IMultiCodeGenerator<CsCallable, MemberDeclarationSyntax>
    {
        private readonly GlobalNamespaceProvider globalNamespace;
        private readonly IGeneratorRegistry generators;

        public ShadowCallbackGenerator(IGeneratorRegistry generators, GlobalNamespaceProvider globalNamespace)
            : base(globalNamespace)
        {
            this.generators = generators;
            this.globalNamespace = globalNamespace;
        }

        public IEnumerable<MemberDeclarationSyntax> GenerateCode(CsCallable csElement)
        {
            yield return GenerateDelegateDeclaration(csElement);
            yield return GenerateShadowCallback(csElement);
        }

        private DelegateDeclarationSyntax GenerateDelegateDeclaration(CsCallable csElement)
        {
            return DelegateDeclaration(ParseTypeName(csElement.Interop.ReturnType.TypeName), $"{csElement.Name}Delegate")
                .AddAttributeLists(
                    AttributeList(
                        SingletonSeparatedList(
                            Attribute(
                                ParseName("System.Runtime.InteropServices.UnmanagedFunctionPointerAttribute"))
                            .AddArgumentListArguments(
                                AttributeArgument(
                                    MemberAccessExpression(
                                        SyntaxKind.SimpleMemberAccessExpression,
                                        MemberAccessExpression(
                                            SyntaxKind.SimpleMemberAccessExpression,
                                            MemberAccessExpression(
                                                SyntaxKind.SimpleMemberAccessExpression,
                                                MemberAccessExpression(
                                                    SyntaxKind.SimpleMemberAccessExpression,
                                                    IdentifierName("System"),
                                                    IdentifierName("Runtime")),
                                                IdentifierName("InteropServices")),
                                            IdentifierName("CallingConvention")),
                                        IdentifierName(csElement.Interop.CallingConvention)))))))
            .WithParameterList(
                ParameterList(
                    (csElement is CsMethod ?
                        SingletonSeparatedList(
                            Parameter(Identifier("thisObject"))
                            .WithType(ParseTypeName("System.IntPtr")))
                        : default)
                    .AddRange(
                        csElement.Interop.ParameterTypes
                            .Select((type, i) =>
                                Parameter(Identifier($"arg{i}"))
                                .WithType(ParseTypeName(type.TypeName)))
                        )))
            .WithModifiers(TokenList(Token(SyntaxKind.PrivateKeyword)));
        }

        private MethodDeclarationSyntax GenerateShadowCallback(CsCallable csElement)
        {
            var methodDecl = MethodDeclaration(
                ParseTypeName(csElement.Interop.ReturnType.TypeName),
                csElement.Name)
                .WithModifiers(
                    TokenList(
                        Token(SyntaxKind.PrivateKeyword),
                        Token(SyntaxKind.StaticKeyword),
                        Token(SyntaxKind.UnsafeKeyword)));

            if (csElement is CsMethod)
            {
                methodDecl = methodDecl
                    .AddParameterListParameters(
                        Parameter(Identifier("thisObject"))
                        .WithType(ParseTypeName("System.IntPtr")));
            }

            IEnumerable<InteropType> nativeParameters = csElement.Interop.ParameterTypes;

            if (csElement.IsReturnStructLarge)
            {
                nativeParameters = nativeParameters.Skip(1);
                methodDecl = methodDecl
                    .AddParameterListParameters(
                        Parameter(Identifier("returnSlot"))
                        .WithType(PointerType(PredefinedType(Token(SyntaxKind.VoidKeyword)))));
            }

            methodDecl = methodDecl
                .AddParameterListParameters(
                    nativeParameters
                            .Select((type, i) =>
                                Parameter(Identifier($"param{i}"))
                                .WithType(ParseTypeName(type.TypeName)))
                            .ToArray());

            var statements = new List<StatementSyntax>();

            statements.AddRange(generators.ReverseCallableProlog.GenerateCode(csElement));
            
            if (csElement is CsMethod)
            {
                statements.Add(LocalDeclarationStatement(
                    VariableDeclaration(
                        IdentifierName(csElement.Parent.Name))
                    .WithVariables(
                        SingletonSeparatedList(
                            VariableDeclarator(
                                Identifier("@this"))
                            .WithInitializer(
                                EqualsValueClause(
                                    CastExpression(
                                        IdentifierName(csElement.Parent.Name),
                                        MemberAccessExpression(
                                            SyntaxKind.SimpleMemberAccessExpression,
                                            InvocationExpression(
                                                GenericName(
                                                    Identifier("ToShadow"))
                                                .WithTypeArgumentList(
                                                    TypeArgumentList(
                                                        SingletonSeparatedList<TypeSyntax>(
                                                            IdentifierName(csElement.GetParent<CsInterface>().ShadowName)))))
                                            .WithArgumentList(
                                                ArgumentList(
                                                    SingletonSeparatedList(
                                                        Argument(
                                                            IdentifierName("thisObject"))))),
                                            IdentifierName("Callback")))))))));
            }

            foreach (var param in csElement.Parameters)
            {
                if (param.IsIn || param.IsRefIn || param.IsRef)
                {
                    var marshalFromNative = generators.MarshalFromNative.GenerateCode(param);
                    if (marshalFromNative != null)
                    {
                        statements.Add(marshalFromNative);
                    }
                }
            }
            
            var managedArguments = new List<ArgumentSyntax>();

            foreach (var param in csElement.Parameters)
            {
                var managedArgument = Argument(IdentifierName(param.Name));
                var managedParam = generators.Parameter.GenerateCode(param);

                if (managedParam.ChildTokens().Any(token => token.RawKind == (int)SyntaxKind.RefKeyword))
                {
                    managedArguments.Add(managedArgument
                        .WithRefOrOutKeyword(Token(SyntaxKind.RefKeyword)));
                }
                else if (managedParam.ChildTokens().Any(token => token.RawKind == (int)SyntaxKind.OutKeyword))
                {
                    managedArguments.Add(managedArgument
                        .WithRefOrOutKeyword(Token(SyntaxKind.OutKeyword)));
                }
                else
                {
                    managedArguments.Add(managedArgument);
                }
            }

            var callableName = csElement is CsFunction
                ? (ExpressionSyntax)IdentifierName(csElement.Name)
                : MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression,
                    IdentifierName("@this"),
                    IdentifierName(csElement.Name));

            var invocation = InvocationExpression(callableName, ArgumentList(SeparatedList(managedArguments)));

            var returnValueNeedsMarshalling = NeedsMarshalling(csElement.ReturnValue);

            if (!csElement.HasReturnType || (csElement.HideReturnType && !csElement.ForceReturnType))
            {
                statements.Add(ExpressionStatement(invocation));
            }
            else
            {
                statements.Add(ExpressionStatement(
                    AssignmentExpression(SyntaxKind.SimpleAssignmentExpression,
                        IdentifierName(csElement.ReturnValue.Name),
                        invocation)));

                if (returnValueNeedsMarshalling)
                {
                    // Bool-to-int is special cased in marshalling so we need to special case it here as well.
                    if (csElement.ReturnValue.IsBoolToInt)
                    {
                        statements.Add(ExpressionStatement(
                            AssignmentExpression(SyntaxKind.SimpleAssignmentExpression,
                                GetMarshalStorageLocation(csElement.ReturnValue),
                                CastExpression(GetMarshalTypeSyntax(csElement.ReturnValue),
                                    ParenthesizedExpression(
                                    ConditionalExpression(
                                        IdentifierName(csElement.ReturnValue.Name),
                                        LiteralExpression(SyntaxKind.NumericLiteralExpression,
                                            Literal(1)),
                                        LiteralExpression(SyntaxKind.NumericLiteralExpression,
                                            Literal(0))
                                ))))));
                    }
                    else
                    {
                        statements.Add(generators.MarshalToNative.GenerateCode(csElement.ReturnValue));
                    }
                }
            }

            foreach (var param in csElement.Parameters)
            {
                if (param.IsOut || param.IsRef)
                {
                    var marshalToNative = generators.MarshalToNative.GenerateCode(param);
                    if (marshalToNative != null)
                    {
                        statements.Add(marshalToNative);
                    }
                }
            }

            var nativeReturnLocation = returnValueNeedsMarshalling
                                ? GetMarshalStorageLocation(csElement.ReturnValue)
                                : IdentifierName(csElement.ReturnValue.Name);

            if (csElement.HasReturnType && (!csElement.HideReturnType || csElement.ForceReturnType))
            {
                statements.Add(
                    ReturnStatement(csElement.IsReturnStructLarge ?
                        IdentifierName("returnSlot")
                        : nativeReturnLocation));
            }

            var exceptionVariableIdentifier = Identifier("__exception__");

            var catchClause = CatchClause()
                .WithDeclaration(
                    CatchDeclaration(ParseTypeName("System.Exception")))
                .WithBlock(Block());

            if (csElement.ReturnValue.PublicType.QualifiedName == globalNamespace.GetTypeName(WellKnownName.Result))
            {
                catchClause = catchClause
                    .WithDeclaration(catchClause.Declaration.WithIdentifier(exceptionVariableIdentifier))
                    .WithBlock(
                        Block(
                            ReturnStatement(
                                MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression,
                                    InvocationExpression(
                                        MemberAccessExpression(
                                            SyntaxKind.SimpleMemberAccessExpression,
                                            globalNamespace.GetTypeNameSyntax(WellKnownName.Result),
                                            IdentifierName("GetResultFromException")))
                                    .WithArgumentList(
                                        ArgumentList(
                                            SingletonSeparatedList(
                                                Argument(
                                                    IdentifierName(exceptionVariableIdentifier))))),
                                    IdentifierName("Code"))
                        )));

                if (csElement.HideReturnType && !csElement.ForceReturnType)
                {
                    statements.Add(ReturnStatement(
                            MemberAccessExpression(
                                SyntaxKind.SimpleMemberAccessExpression,
                                MemberAccessExpression(
                                    SyntaxKind.SimpleMemberAccessExpression,
                                    globalNamespace.GetTypeNameSyntax(WellKnownName.Result),
                                    IdentifierName("Ok")),
                                IdentifierName("Code"))));
                }
            }
            else if (csElement.HasReturnType)
            {
                var returnStatement = csElement.IsReturnStructLarge ?
                    ReturnStatement(IdentifierName("returnSlot"))
                    : ReturnStatement(DefaultExpression(GetMarshalTypeSyntax(csElement.ReturnValue)));
                catchClause = catchClause.WithBlock(Block(returnStatement));
            }

            return methodDecl.WithBody(
                Block(
                    TryStatement()
                    .WithBlock(Block(statements))
                    .WithCatches(SingletonList(catchClause))));
        }
    }
}