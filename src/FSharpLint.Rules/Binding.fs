﻿(*
    FSharpLint, a linter for F#.
    Copyright (C) 2014 Matthew Mcveigh

    This program is free software: you can redistribute it and/or modify
    it under the terms of the GNU General Public License as published by
    the Free Software Foundation, either version 3 of the License, or
    (at your option) any later version.

    This program is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
    GNU General Public License for more details.

    You should have received a copy of the GNU General Public License
    along with this program.  If not, see <http://www.gnu.org/licenses/>.
*)

namespace FSharpLint.Rules

module Binding =
    
    open Microsoft.FSharp.Compiler.Ast
    open Microsoft.FSharp.Compiler.Range
    open Microsoft.FSharp.Compiler.SourceCodeServices
    open FSharpLint.Framework.Ast
    open FSharpLint.Framework.Configuration
    open FSharpLint.Framework.LoadVisitors

    [<Literal>]
    let AnalyserName = "FSharpLint.Binding"

    let isRuleEnabled config ruleName =
        isRuleEnabled config AnalyserName ruleName |> Option.isSome
            
    /// Checks if any code uses 'let _ = ...' and suggests to use the ignore function.
    let checkForBindingToAWildcard visitorInfo (astNode:CurrentNode) pattern range =
        if "FavourIgnoreOverLetWild" |> isRuleEnabled visitorInfo.Config && astNode.IsSuppressed(AnalyserName, "FavourIgnoreOverLetWild") |> not then
            let rec findWildAndIgnoreParens = function
                | SynPat.Paren(pattern, _) -> findWildAndIgnoreParens pattern
                | SynPat.Wild(_) -> true
                | _ -> false
                
            if findWildAndIgnoreParens pattern then
                visitorInfo.PostError range (FSharpLint.Framework.Resources.GetString("RulesFavourIgnoreOverLetWildError"))


    let checkForWildcardNamedWithAsPattern visitorInfo (astNode:CurrentNode) pattern =
        if "WildcardNamedWithAsPattern" |> isRuleEnabled visitorInfo.Config && astNode.IsSuppressed(AnalyserName, "WildcardNamedWithAsPattern") |> not then
            match pattern with
                | SynPat.Named(SynPat.Wild(wildcardRange), _, _, _, range) when wildcardRange <> range ->
                    visitorInfo.PostError range (FSharpLint.Framework.Resources.GetString("RulesWildcardNamedWithAsPattern"))
                | _ -> ()

    let checkForUselessBinding visitorInfo (checkFile:FSharpCheckFileResults) (astNode:CurrentNode) pattern expr range =
        if "UselessBinding" |> isRuleEnabled visitorInfo.Config && astNode.IsSuppressed(AnalyserName, "UselessBinding") |> not then
            let rec findBindingIdentifier = function
                | SynPat.Paren(pattern, _) -> findBindingIdentifier pattern
                | SynPat.Named(_, ident, _, _, _) -> Some(ident)
                | _ -> None

            let rec exprIdentMatchesBindingIdent (bindingIdent:Ident) = function
                | SynExpr.Paren(expr, _, _, _) -> 
                    exprIdentMatchesBindingIdent bindingIdent expr
                | SynExpr.Ident(ident) ->
                    let isSymbolMutable (ident:Ident) =
                        let symbol =
                            checkFile.GetSymbolUseAtLocation(ident.idRange.StartLine, ident.idRange.EndColumn, "", [ident.idText])
                                |> Async.RunSynchronously

                        let isMutable (symbol:FSharpSymbolUse) = (symbol.Symbol :?> FSharpMemberOrFunctionOrValue).IsMutable

                        symbol |> Option.exists isMutable

                    ident.idText = bindingIdent.idText && isSymbolMutable ident |> not
                | _ -> false
                
            findBindingIdentifier pattern |> Option.iter (fun bindingIdent ->
                if exprIdentMatchesBindingIdent bindingIdent expr then
                    visitorInfo.PostError range (FSharpLint.Framework.Resources.GetString("RulesUselessBindingError")))

    let checkTupleOfWildcards visitorInfo (astNode:CurrentNode) pattern identifier =
        if "TupleOfWildcards" |> isRuleEnabled visitorInfo.Config && astNode.IsSuppressed(AnalyserName, "TupleOfWildcards") |> not then
            let rec isWildcard = function
                | SynPat.Paren(pattern, _) -> isWildcard pattern
                | SynPat.Wild(_) -> true
                | _ -> false

            let constructorString numberOfWildcards =
                let constructorName = identifier |> String.concat "."
                let arguments = [ for i in 1..numberOfWildcards -> "_" ] |> String.concat ", "
                constructorName + "(" + arguments + ")"

            match pattern with
                | SynPat.Tuple(patterns, range) when List.length patterns > 1 && patterns |> List.forall isWildcard ->
                    let errorFormat = FSharpLint.Framework.Resources.GetString("RulesTupleOfWildcardsError")
                    let refactorFrom, refactorTo = constructorString(List.length patterns), constructorString 1
                    let error = System.String.Format(errorFormat, refactorFrom, refactorTo)
                    visitorInfo.PostError range error
                | _ -> ()
    
    let visitor visitorInfo checkFile (astNode:CurrentNode) = 
        match astNode.Node with
            | AstNode.Binding(SynBinding.Binding(_, _, _, isMutable, _, _, _, pattern, _, expr, range, _)) -> 
                checkForBindingToAWildcard visitorInfo astNode pattern range
                if not isMutable then
                    checkForUselessBinding visitorInfo checkFile astNode pattern expr range
            | AstNode.Pattern(SynPat.Named(SynPat.Wild(_), _, _, _, _) as pattern) ->
                checkForWildcardNamedWithAsPattern visitorInfo astNode pattern
            | AstNode.Pattern(SynPat.LongIdent(identifier, _, _, SynConstructorArgs.Pats([SynPat.Paren(SynPat.Tuple(_) as pattern, _)]), _, _)) ->
                let identifier = identifier.Lid |> List.map (fun x -> x.idText)
                checkTupleOfWildcards visitorInfo astNode pattern identifier
            | _ -> ()

        Continue

    type RegisterBindingVisitor() = 
        let plugin =
            {
                Name = AnalyserName
                Visitor = Ast(visitor)
            }

        interface IRegisterPlugin with
            member this.RegisterPlugin with get() = plugin