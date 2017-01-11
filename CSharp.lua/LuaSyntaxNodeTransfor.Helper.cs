/*
Copyright 2016 YANG Huan (sy.yanghuan@gmail.com).
Copyright 2016 Redmoon Inc.

Licensed under the Apache License, Version 2.0 (the "License");
you may not use this file except in compliance with the License.
You may obtain a copy of the License at

    http://www.apache.org/licenses/LICENSE-2.0

Unless required by applicable law or agreed to in writing, software
distributed under the License is distributed on an "AS IS" BASIS,
WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
See the License for the specific language governing permissions and
limitations under the License.
*/

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics.Contracts;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using CSharpLua.LuaAst;

namespace CSharpLua {
    public sealed partial class LuaSyntaxNodeTransfor {
        private static readonly Regex codeTemplateRegex_ = new Regex(@"(,?\s*)\{(\*?[\w|^]+)\}", RegexOptions.Compiled);
        private Dictionary<ISymbol, string> localReservedNames_ = new Dictionary<ISymbol, string>();
        private int localMappingCounter_;

        private abstract class LuaSyntaxSearcher : CSharpSyntaxWalker {
            private sealed class FoundException : Exception {
            }
            protected void Found() {
                throw new FoundException();
            }

            public bool Find(SyntaxNode root) {
                try {
                    Visit(root);
                }
                catch(FoundException) {
                    return true;
                }
                return false;
            }
        }

        private sealed class LocalVarSearcher : LuaSyntaxSearcher {
            private string name_;

            public LocalVarSearcher(string name) {
                name_ = name;
            }

            public override void VisitParameter(ParameterSyntax node) {
                if(node.Identifier.ValueText == name_) {
                    Found();
                }
            }

            public override void VisitVariableDeclarator(VariableDeclaratorSyntax node) {
                if(node.Identifier.ValueText == name_) {
                    Found();
                }
            }
        }

        private bool IsLocalVarExists(string name, MethodDeclarationSyntax root) {
            LocalVarSearcher searcher = new LocalVarSearcher(name);
            return searcher.Find(root);
        }

        private string GetNewIdentifierName(string name, int index) {
            switch(index) {
                case 0:
                    return name;
                case 1:
                    return name + "_";
                case 2:
                    return "_" + name;
                default:
                    return name + (index - 2);
            }
        }

        private SyntaxNode FindParent(SyntaxNode node, Func<SyntaxNode, bool> macth) {
            var parent = node.Parent;
            while(true) {
                if(macth(parent)) {
                    return parent;
                }
                parent = parent.Parent;
            }
        }

        private SyntaxNode FindParent(SyntaxNode node, SyntaxKind kind) {
            return FindParent(node, i => i.IsKind(kind));
        }

        private string GetUniqueIdentifier(string name, SyntaxNode node, int index = 0) {
            var root = (MethodDeclarationSyntax)FindParent(node, SyntaxKind.MethodDeclaration);
            while(true) {
                string newName = GetNewIdentifierName(name, index);
                bool exists = IsLocalVarExists(newName, root);
                if(!exists) {
                    return newName;
                }
                ++index;
            }
        }

        private bool CheckReservedWord(ref string name, SyntaxNode node) {
            if(LuaSyntaxNode.IsReservedWord(name)) {
                name = GetUniqueIdentifier(name, node, 1);
                AddReservedMapping(name, node);
                return true;
            }
            return false;
        }

        private void AddReservedMapping(string name, SyntaxNode node) {
            ISymbol symbol = semanticModel_.GetDeclaredSymbol(node);
            Contract.Assert(symbol != null);
            localReservedNames_.Add(symbol, name);
        }

        private void CheckParameterName(ref LuaParameterSyntax parameter, ParameterSyntax node) {
            string name = parameter.Identifier.ValueText;
            bool isReserved = CheckReservedWord(ref name, node);
            if(isReserved) {
                parameter = new LuaParameterSyntax(new LuaIdentifierNameSyntax(name));
            }
        }

        private void CheckVariableDeclaratorName(ref LuaIdentifierNameSyntax identifierName, VariableDeclaratorSyntax node) {
            string name = identifierName.ValueText;
            bool isReserved = CheckReservedWord(ref name, node);
            if(isReserved) {
                identifierName = new LuaIdentifierNameSyntax(name);
            }
        }

        private void CheckReservedWord(ref string name, ISymbol symbol) {
            if(LuaSyntaxNode.IsReservedWord(name)) {
                name = localReservedNames_[symbol];
            }
        }

        private int GetConstructorIndex(IMethodSymbol constructorSymbol) {
            if(constructorSymbol.IsFromCode()) {
                var typeSymbol = (INamedTypeSymbol)constructorSymbol.ReceiverType;
                if(typeSymbol.Constructors.Length > 1) {
                    int index = typeSymbol.Constructors.IndexOf(constructorSymbol);
                    Contract.Assert(index != -1);
                    int ctroCounter = index + 1;
                    return ctroCounter;
                }
            }
            return 0;
        }

        private sealed class ContinueSearcher : LuaSyntaxSearcher {
            public override void VisitContinueStatement(ContinueStatementSyntax node) {
                Found();
            }
        }

        private bool IsContinueExists(SyntaxNode node) {
            ContinueSearcher searcher = new ContinueSearcher();
            return searcher.Find(node);
        }

        private sealed class ReturnStatementSearcher : LuaSyntaxSearcher {
            public override void VisitReturnStatement(ReturnStatementSyntax node) {
                Found();
            }
        }

        private bool IsReturnExists(SyntaxNode node) {
            ReturnStatementSearcher searcher = new ReturnStatementSearcher();
            return searcher.Find(node);
        }

        private int GetCaseLabelIndex(GotoStatementSyntax node) {
            var switchStatement = (SwitchStatementSyntax)FindParent(node, SyntaxKind.SwitchStatement);
            int index = 0;
            foreach(var section in switchStatement.Sections) {
               bool isFound = section.Labels.Any(i => {
                    if(i.IsKind(SyntaxKind.CaseSwitchLabel)) {
                        var label = (CaseSwitchLabelSyntax)i;
                        if(label.Value.ToString() == node.Expression.ToString()) {
                            return true;
                        }
                    }
                    return false;
                });
                if(isFound) {
                    return index;
                }
            }
            throw new InvalidOperationException();
        }

        private LuaExpressionSyntax BuildCodeTemplateExpression(string codeTemplate, ExpressionSyntax targetExpression) {
            return BuildCodeTemplateExpression(codeTemplate, targetExpression, Array.Empty<ExpressionSyntax>(), ImmutableArray<ITypeSymbol>.Empty);
        }

        private void AddCodeTemplateExpression(LuaExpressionSyntax expression, string comma, LuaCodeTemplateExpressionSyntax codeTemplateExpression) {
            if(!string.IsNullOrEmpty(comma)) {
                codeTemplateExpression.Codes.Add(new LuaIdentifierNameSyntax(comma));
            }
            codeTemplateExpression.Codes.Add(expression);
        }

        private LuaExpressionSyntax BuildCodeTemplateExpression(string codeTemplate, ExpressionSyntax targetExpression, IList<ExpressionSyntax> arguments, ImmutableArray<ITypeSymbol> typeArguments) {
            LuaCodeTemplateExpressionSyntax codeTemplateExpression = new LuaCodeTemplateExpressionSyntax();

            var matchs = codeTemplateRegex_.Matches(codeTemplate);
            int prevIndex = 0;
            foreach(Match match in matchs) {
                if(match.Index > prevIndex) {
                    string prevToken = codeTemplate.Substring(prevIndex, match.Index - prevIndex);
                    codeTemplateExpression.Codes.Add(new LuaIdentifierNameSyntax(prevToken));
                }
                string comma = match.Groups[1].Value;
                string key = match.Groups[2].Value;
                if(key == "this") {
                    AddCodeTemplateExpression(BuildMemberAccessTargetExpression(targetExpression), comma, codeTemplateExpression);
                }
                else if(key == "class") {
                    var type = semanticModel_.GetTypeInfo(targetExpression).Type;
                    var typeName = XmlMetaProvider.GetTypeName(type);
                    AddCodeTemplateExpression(typeName, comma, codeTemplateExpression);
                }
                else if(key[0] == '^') {
                    int typeIndex;
                    if(int.TryParse(key.Substring(1), out typeIndex)) {
                        var typeArgument = typeArguments.GetOrDefault(typeIndex);
                        if(typeArgument != null) {
                            var typeName = XmlMetaProvider.GetTypeName(typeArgument);
                            AddCodeTemplateExpression(typeName, comma, codeTemplateExpression);
                        }
                    }
                }
                else if(key[0] == '*') {
                    int paramsIndex;
                    if(int.TryParse(key.Substring(1), out paramsIndex)) {
                        LuaCodeTemplateParamsExpressionSyntax paramsExpression = new LuaCodeTemplateParamsExpressionSyntax();
                        foreach(var argument in arguments.Skip(paramsIndex)) {
                            var argumentExpression = (LuaExpressionSyntax)argument.Accept(this);
                            paramsExpression.Expressions.Add(argumentExpression);
                        }
                        if(paramsExpression.Expressions.Count > 0) {
                            AddCodeTemplateExpression(paramsExpression, comma, codeTemplateExpression);
                        }
                    }
                }
                else {
                    int argumentIndex;
                    if(int.TryParse(key, out argumentIndex)) {
                        var argument = arguments.GetOrDefault(argumentIndex);
                        if(argument != null) {
                            var argumentExpression = (LuaExpressionSyntax)argument.Accept(this);
                            AddCodeTemplateExpression(argumentExpression, comma, codeTemplateExpression);
                        }
                    }
                }
                prevIndex = match.Index + match.Length;
            }

            if(prevIndex < codeTemplate.Length) {
                string last = codeTemplate.Substring(prevIndex);
                codeTemplateExpression.Codes.Add(new LuaIdentifierNameSyntax(last));
            }

            return codeTemplateExpression;
        }

        private bool IsPropertyField(IPropertySymbol symbol) {
            return symbol.IsPropertyField() || XmlMetaProvider.IsPropertyField(symbol);
        }

        private INamedTypeSymbol GetTypeDeclarationSymbol(SyntaxNode node) {
            var typeDeclaration = (TypeDeclarationSyntax)FindParent(node, i => i.IsKind(SyntaxKind.ClassDeclaration) || i.IsKind(SyntaxKind.StructDeclaration));
            return semanticModel_.GetDeclaredSymbol(typeDeclaration);
        }

        private bool IsInternalMember(SyntaxNode node, ISymbol symbol) {
            bool isVirtual = symbol.IsOverridable() && !symbol.ContainingType.IsSealed;
            if(!isVirtual) {
                var typeSymbol = GetTypeDeclarationSymbol(node);
                if(typeSymbol == symbol.ContainingType) {
                    return true;
                }
            }
            return false;
        }

        private LuaInvocationExpressionSyntax BuildEmptyArray(LuaExpressionSyntax baseType) {
            return new LuaInvocationExpressionSyntax(LuaIdentifierNameSyntax.ArrayEmpty, baseType);
        }

        private LuaLiteralExpressionSyntax GetConstLiteralExpression(object constantValue) {
            if(constantValue != null) {
                var code = Type.GetTypeCode(constantValue.GetType());
                switch(code) {
                    case TypeCode.Char: {
                            return new LuaCharacterLiteralExpression((char)constantValue);
                        }
                    case TypeCode.String: {
                            return BuildStringLiteralExpression((string)constantValue);
                        }
                    default: {
                            return new LuaIdentifierLiteralExpressionSyntax(constantValue.ToString());
                        }
                }
            }
            else {
                return new LuaIdentifierLiteralExpressionSyntax(LuaIdentifierNameSyntax.Nil);
            }
        }

        private LuaLiteralExpressionSyntax GetConstLiteralExpression(IFieldSymbol constField) {
            Contract.Assert(constField.HasConstantValue);
            if(constField.Type.SpecialType == SpecialType.System_Char) {
                return new LuaCharacterLiteralExpression((char)constField.ConstantValue);
            }
            else {
                var constExpression = GetConstLiteralExpression(constField.ConstantValue);
                string identifierToken = constField.ContainingType.Name + '.' + constField.Name;
                return new LuaConstLiteralExpression(constExpression, identifierToken);
            }
        }

        private LuaLiteralExpressionSyntax BuildStringLiteralTokenExpression(SyntaxToken token) {
            if(token.Text[0] == '@') {
                return BuildVerbatimStringExpression(token.ValueText);
            }
            else {
                return new LuaIdentifierLiteralExpressionSyntax(token.Text);
            }
        }

        private LuaIdentifierLiteralExpressionSyntax BuildStringLiteralExpression(string value) {
            string text = SyntaxFactory.Literal(value).Text;
            return new LuaIdentifierLiteralExpressionSyntax(text);
        }

        private LuaVerbatimStringLiteralExpressionSyntax BuildVerbatimStringExpression(string value) {
            const string kCloseBracket = LuaSyntaxNode.Tokens.CloseBracket;
            char equals = LuaSyntaxNode.Tokens.Equals[0];
            int count = 0;
            while(true) {
                string closeToken = kCloseBracket + new string(equals, count) + kCloseBracket;
                if(!value.Contains(closeToken)) {
                    break;
                }
                ++count;
            }
            if(value[0] == '\n') {
                value = '\n' + value;
            }
            return new LuaVerbatimStringLiteralExpressionSyntax(value, count);
        }

        private enum CallerAttributeKind {
            None,
            Line,
            Member,
            FilePath,
        }

        private CallerAttributeKind GetCallerAttributeKind(INamedTypeSymbol typeSymbol) {
            switch(typeSymbol.ToString()) {
                case "System.Runtime.CompilerServices.CallerLineNumberAttribute":
                    return CallerAttributeKind.Line;
                case "System.Runtime.CompilerServices.CallerMemberNameAttribute":
                    return CallerAttributeKind.Member;
                case "System.Runtime.CompilerServices.CallerFilePathAttribute":
                    return CallerAttributeKind.FilePath;
                default:
                    return CallerAttributeKind.None;
            }
        }

        private CallerAttributeKind GetCallerAttributeKind(IParameterSymbol parameter) {
            foreach(var attribute in parameter.GetAttributes()) {
                var callerKind = GetCallerAttributeKind(attribute.AttributeClass);
                if(callerKind != CallerAttributeKind.None) {
                    return callerKind;
                }
            }
            return CallerAttributeKind.None;
        }

        private bool IsCallerAttribute(AttributeSyntax attribute) {
            var method = semanticModel_.GetSymbolInfo(attribute.Name).Symbol;
            return GetCallerAttributeKind(method.ContainingType) != CallerAttributeKind.None;
        }

        private LuaExpressionSyntax CheckCallerAttribute(IParameterSymbol parameter, InvocationExpressionSyntax node) {
            var kind = GetCallerAttributeKind(parameter);
            switch(kind) {
                case CallerAttributeKind.Line: {
                        var lineSpan = node.SyntaxTree.GetLineSpan(node.Span);
                        return new LuaIdentifierNameSyntax(lineSpan.StartLinePosition.Line + 1);
                    }
                case CallerAttributeKind.Member: {
                        var parentMethod = (MethodDeclarationSyntax)FindParent(node, SyntaxKind.MethodDeclaration);
                        return new LuaStringLiteralExpressionSyntax(new LuaIdentifierNameSyntax(parentMethod.Identifier.ValueText));
                    }
                case CallerAttributeKind.FilePath: {
                        return BuildStringLiteralExpression(node.SyntaxTree.FilePath);
                    }
                default:
                    return null;
            }
        }
    }
}
