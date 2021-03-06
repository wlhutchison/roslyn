﻿// Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Composition;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp.CodeStyle;
using Microsoft.CodeAnalysis.CSharp.CodeStyle.TypeStyle;
using Microsoft.CodeAnalysis.CSharp.Extensions;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Editing;
using Microsoft.CodeAnalysis.Formatting;
using Microsoft.CodeAnalysis.Options;
using Microsoft.CodeAnalysis.Shared.Extensions;
using Microsoft.CodeAnalysis.Shared.Utilities;
using Microsoft.CodeAnalysis.Text;
using Roslyn.Utilities;

namespace Microsoft.CodeAnalysis.CSharp.InlineDeclaration
{
    [ExportCodeFixProvider(LanguageNames.CSharp), Shared]
    internal partial class CSharpInlineDeclarationCodeFixProvider : SyntaxEditorBasedCodeFixProvider
    {
        public override ImmutableArray<string> FixableDiagnosticIds
            => ImmutableArray.Create(IDEDiagnosticIds.InlineDeclarationDiagnosticId);

        public override Task RegisterCodeFixesAsync(CodeFixContext context)
        {
            context.RegisterCodeFix(new MyCodeAction(
                c => FixAsync(context.Document, context.Diagnostics.First(), c)),
                context.Diagnostics);
            return SpecializedTasks.EmptyTask;
        }

        protected override async Task FixAllAsync(
            Document document, ImmutableArray<Diagnostic> diagnostics, 
            SyntaxEditor editor, CancellationToken cancellationToken)
        {
            var options = document.Project.Solution.Options;

            // Attempt to use an out-var declaration if that's the style the user prefers.
            // Note: if using 'var' would cause a problem, we will use the actual type
            // of hte local.  This is necessary in some cases (for example, when the
            // type of the out-var-decl affects overload resolution or generic instantiation).

            foreach (var diagnostic in diagnostics)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await AddEditsAsync(
                    document, editor, diagnostic, 
                    options, cancellationToken).ConfigureAwait(false);
            }
        }

        private async Task AddEditsAsync(
            Document document, SyntaxEditor editor, Diagnostic diagnostic, 
            OptionSet options, CancellationToken cancellationToken)
        {
            var semanticModel = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
            var sourceText = await document.GetTextAsync(cancellationToken).ConfigureAwait(false);

            // Recover the nodes we care about.
            var declaratorLocation = diagnostic.AdditionalLocations[0];
            var identifierLocation = diagnostic.AdditionalLocations[1];
            var invocationOrCreationLocation = diagnostic.AdditionalLocations[2];
            var outArgumentContainingStatementLocation = diagnostic.AdditionalLocations[3];

            var root = declaratorLocation.SourceTree.GetRoot(cancellationToken);

            var declarator = (VariableDeclaratorSyntax)declaratorLocation.FindNode(cancellationToken);
            var identifier = (IdentifierNameSyntax)identifierLocation.FindNode(cancellationToken);
            var invocationOrCreation = (ExpressionSyntax)invocationOrCreationLocation.FindNode(
                getInnermostNodeForTie: true, cancellationToken: cancellationToken);
            var outArgumentContainingStatement = (StatementSyntax)outArgumentContainingStatementLocation.FindNode(cancellationToken);

            var declaration = (VariableDeclarationSyntax)declarator.Parent;
            var singleDeclarator = declaration.Variables.Count == 1;

            if (singleDeclarator)
            {
                // This was a local statement with a single variable in it.  Just Remove 
                // the entire local declaration statement.  Note that comments belonging to
                // this local statement will be moved to be above the statement containing
                // the out-var. 
                editor.RemoveNode(declaration.Parent);
            }
            else
            {
                // Otherwise, just remove the single declarator. Note: we'll move the comments
                // 'on' the declarator to the out-var location.  This is a little bit trickier
                // than normal due to how our comment-association rules work.  i.e. if you have:
                //
                //      var /*c1*/ i /*c2*/, /*c3*/ j /*c4*/;
                //
                // In this case 'c1' is owned by the 'var' token, not 'i', and 'c3' is owned by 
                // the comment token not 'j'.  

                editor.RemoveNode(declarator);
                if (declarator == declaration.Variables[0])
                {
                    // If we're removing the first declarator, and it's on the same line
                    // as the previous token, then we want to remove all the trivia belonging
                    // to the previous token.  We're going to move it along with this declarator.
                    // If we don't, then the comment will stay with the previous token.
                    //
                    // Note that hte moving of the comment happens later on when we make the
                    // declaration expression.
                    if (sourceText.AreOnSameLine(declarator.GetFirstToken(), declarator.GetFirstToken().GetPreviousToken(includeSkipped: true)))
                    {
                        editor.ReplaceNode(
                            declaration.Type, 
                            (t, g) => t.WithTrailingTrivia(SyntaxFactory.ElasticSpace).WithoutAnnotations(Formatter.Annotation));
                    }
                }
            }

            // get the type that we want to put in the out-var-decl based on the user's options.
            // i.e. prefer 'out var' if that is what the user wants.  Note: if we have:
            //
            //      Method(out var x)
            //
            // Then the type is not-apperant, and we shoudl not use var if the user only wants
            // it for apperant types

            var local = (ILocalSymbol)semanticModel.GetDeclaredSymbol(declarator);
            var newType = local.Type.GenerateTypeSyntaxOrVar(options, typeIsApperant: false);

            var declarationExpression = GetDeclarationExpression(
                sourceText, identifier, newType, singleDeclarator ? null : declarator);

            // Check if using out-var changed problem semantics.
            var semanticsChanged = await SemanticsChangedAsync(
                document, declaration, invocationOrCreation, newType,
                identifier, declarationExpression, cancellationToken).ConfigureAwait(false);
            if (semanticsChanged && newType.IsVar)
            {
                // Switching to 'var' changed semantics.  Just use the original type of the local.

                // If the user originally wrote it something other than 'var', then use what they
                // wrote.  Otherwise, synthesize the actual type of the local.
                var explicitType = declaration.Type.IsVar ? local.Type?.GenerateTypeSyntax() : declaration.Type;
                declarationExpression = GetDeclarationExpression(
                    sourceText, identifier, explicitType, singleDeclarator ? null : declarator);
            }

            editor.ReplaceNode(identifier, declarationExpression);

            if (declaration.Variables.Count == 1)
            {
                // If we're removing the declaration entirely, move the leading/trailing comments it 
                // had to sit above the statement containing the out-var declaration.
                var comments = declaration.Parent.GetLeadingTrivia().Concat(declaration.Parent.GetTrailingTrivia())
                                                                    .Where(t => t.IsSingleOrMultiLineComment())
                                                                    .SelectMany(t => ImmutableArray.Create(t, SyntaxFactory.ElasticCarriageReturnLineFeed))
                                                                    .ToImmutableArray();
                if (comments.Length > 0)
                {
                    editor.ReplaceNode(
                        outArgumentContainingStatement,
                        (s, g) => s.WithPrependedLeadingTrivia(comments).WithAdditionalAnnotations(Formatter.Annotation));
                }
            }
        }

        private static DeclarationExpressionSyntax GetDeclarationExpression(
            SourceText sourceText, IdentifierNameSyntax identifier,
            TypeSyntax newType, VariableDeclaratorSyntax declaratorOpt)
        {
            newType = newType.WithoutTrivia().WithAdditionalAnnotations(Formatter.Annotation);
            var designation = SyntaxFactory.SingleVariableDesignation(identifier.Identifier);

            if (declaratorOpt != null)
            {
                // We're removing a single declarator.  Copy any comments it has to the out-var.
                //
                // Note: this is tricky due to comment ownership.  We want hte comments that logically
                // belong to the declarator, even if our syntax model attaches them to other tokens.
                var precedingTrivia = declaratorOpt.GetAllPrecedingTriviaToPreviousToken(
                    sourceText, includePreviousTokenTrailingTriviaOnlyIfOnSameLine: true);
                if (precedingTrivia.Any(t => t.IsSingleOrMultiLineComment()))
                {
                    designation = designation.WithPrependedLeadingTrivia(MassageTrivia(precedingTrivia));
                }

                if (declaratorOpt.GetTrailingTrivia().Any(t => t.IsSingleOrMultiLineComment()))
                {
                    designation = designation.WithAppendedTrailingTrivia(MassageTrivia(declaratorOpt.GetTrailingTrivia()));
                }
            }

            return SyntaxFactory.DeclarationExpression(newType, designation);
        }

        private static IEnumerable<SyntaxTrivia> MassageTrivia(IEnumerable<SyntaxTrivia> triviaList)
        {
            foreach (var trivia in triviaList)
            {
                if (trivia.IsSingleOrMultiLineComment())
                {
                    yield return trivia;
                }
                else if (trivia.IsWhitespace())
                {
                    // Condense whitespace down to single spaces. We don't want things like
                    // indentation spaces to be inserted in the out-var location.  It is appropraite
                    // though to have single spaces to help separate out things like comments and
                    // tokens though.
                    yield return SyntaxFactory.Space;
                }
            }
        }

        private async Task<bool> SemanticsChangedAsync(
            Document document,
            VariableDeclarationSyntax declaration,
            ExpressionSyntax invocationOrCreation,
            TypeSyntax newType,
            IdentifierNameSyntax identifier,
            DeclarationExpressionSyntax declarationExpression,
            CancellationToken cancellationToken)
        {
            if (newType.IsVar)
            {
                // Options want us to use 'var' if we can.  Make sure we didn't change
                // the semantics of teh call by doing this.

                // Find the symbol that the existing invocation points to.
                var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
                var semanticModel = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
                var previousSymbol = semanticModel.GetSymbolInfo(invocationOrCreation).Symbol;

                var annotation = new SyntaxAnnotation();
                var updatedInvocationOrCreation = invocationOrCreation.ReplaceNode(
                    identifier, declarationExpression).WithAdditionalAnnotations(annotation);

                // Note(cyrusn): "https://github.com/dotnet/roslyn/issues/14384" prevents us from just
                // speculatively binding the new expression.  So, instead, we fork things and see if
                // the new symbol we bind to is equivalent to the previous one.
                var newDocument = document.WithSyntaxRoot(
                    root.ReplaceNode(invocationOrCreation, updatedInvocationOrCreation));

                var newRoot = await newDocument.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
                var newSemanticModel = await newDocument.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);

                updatedInvocationOrCreation = (ExpressionSyntax)newRoot.GetAnnotatedNodes(annotation).Single();

                var updatedSymbol = newSemanticModel.GetSymbolInfo(updatedInvocationOrCreation).Symbol;

                if (!SymbolEquivalenceComparer.Instance.Equals(previousSymbol, updatedSymbol))
                {
                    // We're pointing at a new symbol now.  Semantic have changed.
                    return true;
                }
            }

            return false;
        }

        private TypeSyntax GetDeclarationType(
            TypeSyntax type, bool useVarWhenDeclaringLocals, bool useImplicitTypeForIntrinsicTypes)
        {
            if (useVarWhenDeclaringLocals)
            {
                if (useImplicitTypeForIntrinsicTypes ||
                    !TypeStyleHelper.IsPredefinedType(type))
                {
                    return SyntaxFactory.IdentifierName("var");
                }
            }

            return type;
        }

        private class MyCodeAction : CodeAction.DocumentChangeAction
        {
            public MyCodeAction(Func<CancellationToken, Task<Document>> createChangedDocument) 
                : base(FeaturesResources.Inline_variable_declaration,
                       createChangedDocument,
                       FeaturesResources.Inline_variable_declaration)
            {
            }
        }
    }
}