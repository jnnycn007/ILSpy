﻿// Copyright (c) 2011 AlphaSierraPapa for the SharpDevelop Team
// 
// Permission is hereby granted, free of charge, to any person obtaining a copy of this
// software and associated documentation files (the "Software"), to deal in the Software
// without restriction, including without limitation the rights to use, copy, modify, merge,
// publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons
// to whom the Software is furnished to do so, subject to the following conditions:
// 
// The above copyright notice and this permission notice shall be included in all copies or
// substantial portions of the Software.
// 
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED,
// INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR
// PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE
// FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR
// OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER
// DEALINGS IN THE SOFTWARE.

using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

using ICSharpCode.Decompiler.CSharp.Resolver;
using ICSharpCode.Decompiler.CSharp.Syntax;
using ICSharpCode.Decompiler.CSharp.TypeSystem;
using ICSharpCode.Decompiler.Semantics;
using ICSharpCode.Decompiler.TypeSystem;
using ICSharpCode.Decompiler.Util;

namespace ICSharpCode.Decompiler.CSharp.Transforms
{
	/// <summary>
	/// Converts extension method calls into infix syntax.
	/// </summary>
	public class IntroduceExtensionMethods : DepthFirstAstVisitor, IAstTransform
	{
		TransformContext context;
		CSharpResolver resolver;
		CSharpConversions conversions;

		public void Run(AstNode rootNode, TransformContext context)
		{
			this.context = context;
			this.conversions = CSharpConversions.Get(context.TypeSystem);
			InitializeContext(rootNode.Annotation<UsingScope>());
			rootNode.AcceptVisitor(this);
		}

		void InitializeContext(UsingScope usingScope)
		{
			if (!string.IsNullOrEmpty(context.CurrentTypeDefinition?.Namespace))
			{
				foreach (string ns in context.CurrentTypeDefinition.Namespace.Split('.'))
				{
					usingScope = usingScope.WithNestedNamespace(ns);
				}
			}
			var currentContext = new CSharpTypeResolveContext(context.TypeSystem.MainModule, usingScope, context.CurrentTypeDefinition);
			this.resolver = new CSharpResolver(currentContext);
		}

		public override void VisitNamespaceDeclaration(NamespaceDeclaration namespaceDeclaration)
		{
			var usingScope = resolver.CurrentUsingScope;
			foreach (string ident in namespaceDeclaration.Identifiers)
			{
				usingScope = usingScope.WithNestedNamespace(ident);
			}
			var previousResolver = this.resolver;
			try
			{
				this.resolver = this.resolver.WithCurrentUsingScope(usingScope);
				base.VisitNamespaceDeclaration(namespaceDeclaration);
			}
			finally
			{
				this.resolver = previousResolver;
			}
		}

		public override void VisitTypeDeclaration(TypeDeclaration typeDeclaration)
		{
			var previousResolver = this.resolver;
			this.resolver = resolver.WithCurrentTypeDefinition(typeDeclaration.GetSymbol() as ITypeDefinition);
			try
			{
				base.VisitTypeDeclaration(typeDeclaration);
			}
			finally
			{
				this.resolver = previousResolver;
			}
		}

		public override void VisitInvocationExpression(InvocationExpression invocationExpression)
		{
			base.VisitInvocationExpression(invocationExpression);
			if (!CanTransformToExtensionMethodCall(resolver, invocationExpression, out var memberRefExpr,
				out var target, out var firstArgument))
			{
				return;
			}
			var method = (IMethod)invocationExpression.GetSymbol();
			if (firstArgument is DirectionExpression dirExpr)
			{
				if (!context.Settings.RefExtensionMethods || dirExpr.FieldDirection == FieldDirection.Out)
					return;
				firstArgument = dirExpr.Expression;
				target = firstArgument.GetResolveResult();
				dirExpr.Detach();
			}
			else if (firstArgument is NullReferenceExpression)
			{
				Debug.Assert(context.RequiredNamespacesSuperset.Contains(method.Parameters[0].Type.Namespace));
				firstArgument = firstArgument.ReplaceWith(expr => new CastExpression(context.TypeSystemAstBuilder.ConvertType(method.Parameters[0].Type), expr.Detach()));
			}
			if (invocationExpression.Target is IdentifierExpression identifierExpression)
			{
				identifierExpression.Detach();
				memberRefExpr = new MemberReferenceExpression(firstArgument.Detach(), method.Name, identifierExpression.TypeArguments.Detach());
				invocationExpression.Target = memberRefExpr;
			}
			else
			{
				memberRefExpr.Target = firstArgument.Detach();
			}
			if (invocationExpression.GetResolveResult() is CSharpInvocationResolveResult irr)
			{
				// do not forget to update the CSharpInvocationResolveResult => set IsExtensionMethodInvocation == true
				invocationExpression.RemoveAnnotations<CSharpInvocationResolveResult>();
				var newResolveResult = new CSharpInvocationResolveResult(
					irr.TargetResult, irr.Member, irr.Arguments, irr.OverloadResolutionErrors,
					isExtensionMethodInvocation: true, irr.IsExpandedForm, irr.IsDelegateInvocation,
					irr.GetArgumentToParameterMap(), irr.InitializerStatements);
				invocationExpression.AddAnnotation(newResolveResult);
			}
		}

		static bool CanTransformToExtensionMethodCall(CSharpResolver resolver,
			InvocationExpression invocationExpression, out MemberReferenceExpression memberRefExpr,
			out ResolveResult target,
			out Expression firstArgument)
		{
			var method = invocationExpression.GetSymbol() as IMethod;
			memberRefExpr = null;
			target = null;
			firstArgument = null;
			if (method == null || !method.IsExtensionMethod || !invocationExpression.Arguments.Any())
				return false;
			IReadOnlyList<IType> typeArguments;
			switch (invocationExpression.Target)
			{
				case MemberReferenceExpression mre:
					typeArguments = mre.TypeArguments.Any() ? method.TypeArguments : EmptyList<IType>.Instance;
					memberRefExpr = mre;
					break;
				case IdentifierExpression ide:
					typeArguments = ide.TypeArguments.Any() ? method.TypeArguments : EmptyList<IType>.Instance;
					memberRefExpr = null;
					break;
				default:
					return false;
			}

			firstArgument = invocationExpression.Arguments.First();
			if (firstArgument is NamedArgumentExpression)
				return false;
			target = firstArgument.GetResolveResult();
			if (target is ConstantResolveResult crr && crr.ConstantValue == null)
			{
				target = new ConversionResolveResult(method.Parameters[0].Type, crr, Conversion.NullLiteralConversion);
			}
			else if (firstArgument is DirectionExpression de)
			{
				target = de.Expression.GetResolveResult();
			}
			Debug.Assert(target != null);
			ResolveResult[] args = new ResolveResult[invocationExpression.Arguments.Count - 1];
			string[] argNames = null;
			int pos = 0;
			foreach (var arg in invocationExpression.Arguments.Skip(1))
			{
				if (arg is NamedArgumentExpression nae)
				{
					if (argNames == null)
					{
						argNames = new string[args.Length];
					}
					argNames[pos] = nae.Name;
					args[pos] = nae.Expression.GetResolveResult();
				}
				else
				{
					args[pos] = arg.GetResolveResult();
				}
				pos++;
			}
			return resolver.CanTransformToExtensionMethodCall(method, typeArguments, target, args, argNames);
		}
	}
}
