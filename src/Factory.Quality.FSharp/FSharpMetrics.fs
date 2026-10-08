namespace Factory.Quality.FSharp

open FSharp.Compiler.CodeAnalysis
open FSharp.Compiler.Syntax
open FSharp.Compiler.Text
open Factory.Core

/// The F# adapter: parses with FSharp.Compiler.Service (syntax only, no type checking) and turns every function binding
/// and member into the neutral `FunctionMetrics` the ratchet judges. Pure in the source text.
module FSharpMetrics =

    let private checker = lazy (FSharpChecker.Create())

    /// `&&` and `||` as the parser spells them: an operator name, as a plain or a dotted identifier.
    let private (|ShortCircuit|_|) (e: SynExpr) =
        let isOp (name: string) = name = "op_BooleanAnd" || name = "op_BooleanOr"
        match e with
        | SynExpr.Ident id when isOp id.idText -> Some()
        | SynExpr.LongIdent(_, SynLongIdent(id = ids), _, _) when ids |> List.exists (fun i -> isOp i.idText) -> Some()
        | _ -> None

    /// 1 + decision points: if/elif, loops, `&&`/`||`, each match or try-with clause that is not a wildcard.
    let rec private decisions (e: SynExpr) : int =
        let all es = es |> List.sumBy decisions
        let clauses (cs: SynMatchClause list) =
            cs |> List.sumBy (fun (SynMatchClause(pat, guard, body, _, _, _)) ->
                (match pat with SynPat.Wild _ -> 0 | _ -> 1) + (guard |> Option.map decisions |> Option.defaultValue 0) + decisions body)
        match e with
        | SynExpr.IfThenElse(c, t, el, _, _, _, _) -> 1 + decisions c + decisions t + (el |> Option.map decisions |> Option.defaultValue 0)
        | SynExpr.While(_, c, b, _) -> 1 + decisions c + decisions b
        | SynExpr.For(_, _, _, _, a, _, b, body, _) -> 1 + decisions a + decisions b + decisions body
        | SynExpr.ForEach(_, _, _, _, _, en, body, _) -> 1 + decisions en + decisions body
        | SynExpr.Match(_, x, cs, _, _) -> decisions x + clauses cs
        | SynExpr.MatchBang(_, x, cs, _, _) -> decisions x + clauses cs
        | SynExpr.MatchLambda(_, _, cs, _, _) -> clauses cs
        | SynExpr.TryWith(t, cs, _, _, _, _) -> decisions t + clauses cs
        | SynExpr.TryFinally(t, f, _, _, _, _) -> decisions t + decisions f
        | SynExpr.App(_, _, SynExpr.App(_, true, ShortCircuit, l, _), r, _) -> 1 + decisions l + decisions r
        | SynExpr.App(_, _, f, a, _) -> decisions f + decisions a
        | SynExpr.Paren(x, _, _, _) | SynExpr.Typed(x, _, _) | SynExpr.Lazy(x, _) | SynExpr.Do(x, _) | SynExpr.DoBang(x, _)
        | SynExpr.Upcast(x, _, _) | SynExpr.Downcast(x, _, _) | SynExpr.TypeApp(x, _, _, _, _, _, _) | SynExpr.Assert(x, _)
        | SynExpr.YieldOrReturn(_, x, _) | SynExpr.YieldOrReturnFrom(_, x, _) | SynExpr.DotGet(x, _, _, _)
        | SynExpr.ArrayOrListComputed(_, x, _) | SynExpr.ComputationExpr(_, x, _) | SynExpr.New(_, _, x, _) | SynExpr.LongIdentSet(_, x, _) -> decisions x
        | SynExpr.Lambda(_, _, _, body, _, _, _) -> decisions body
        | SynExpr.Sequential(_, _, a, b, _, _) -> decisions a + decisions b
        | SynExpr.Tuple(_, es, _, _) | SynExpr.ArrayOrList(_, es, _) -> all es
        | SynExpr.LetOrUse(_, _, bs, body, _, _) -> (bs |> List.sumBy (fun (SynBinding(expr = x)) -> decisions x)) + decisions body
        | SynExpr.LetOrUseBang(_, _, _, _, x, ands, body, _, _) -> decisions x + (ands |> List.sumBy (fun (SynExprAndBang(body = b)) -> decisions b)) + decisions body
        | SynExpr.Record(_, _, fields, _) -> fields |> List.sumBy (fun (SynExprRecordField(expr = x)) -> x |> Option.map decisions |> Option.defaultValue 0)
        | SynExpr.Set(a, b, _) | SynExpr.DotSet(a, _, b, _) -> decisions a + decisions b
        | _ -> 0

    /// A binding is a function when it takes arguments (or its body is a lambda); a plain value is not.
    let private isFunction (SynBinding(headPat = pat; expr = body)) =
        (match pat with
         | SynPat.LongIdent(argPats = SynArgPats.Pats(_ :: _)) -> true
         | SynPat.LongIdent(argPats = SynArgPats.NamePatPairs(_ :: _, _, _)) -> true
         | _ -> false)
        || (match body with SynExpr.Lambda _ | SynExpr.MatchLambda _ -> true | _ -> false)

    let private nameOf (SynBinding(headPat = pat)) =
        match pat with
        | SynPat.LongIdent(longDotId = SynLongIdent(id = ids)) ->
            let parts = ids |> List.map (fun i -> i.idText)
            // `member _.Total`: the leading `_`/`this` is the self identifier, not part of the name
            match parts with
            | self :: rest when not rest.IsEmpty && (self = "_" || self = "this" || self = "x" || self = "self") -> rest
            | p -> p
        | SynPat.Named(ident = SynIdent(id, _)) -> [ id.idText ]
        | _ -> []

    let rec private bindings (prefix: string list) (decl: SynModuleDecl) : (string list * SynBinding) list =
        match decl with
        | SynModuleDecl.Let(_, bs, _) -> bs |> List.map (fun b -> (prefix, b))
        | SynModuleDecl.NestedModule(SynComponentInfo(longId = id), _, decls, _, _, _) ->
            decls |> List.collect (bindings (prefix @ (id |> List.map (fun i -> i.idText))))
        | SynModuleDecl.Types(types, _) ->
            types |> List.collect (fun (SynTypeDefn(SynComponentInfo(longId = id), repr, members, _, _, _)) ->
                let tprefix = prefix @ (id |> List.map (fun i -> i.idText))
                let inRepr =
                    match repr with
                    | SynTypeDefnRepr.ObjectModel(_, ms, _) -> ms
                    | _ -> []
                (inRepr @ members) |> List.collect (function
                    | SynMemberDefn.Member(b, _) -> [ (tprefix, b) ]
                    | SynMemberDefn.LetBindings(bs, _, _, _) -> bs |> List.map (fun b -> (tprefix, b))
                    | _ -> []))
        | _ -> []

    let measure (file: string) (source: string) : FunctionMetrics list =
        let options = { FSharpParsingOptions.Default with SourceFiles = [| file |] }
        let result = checker.Value.ParseFile(file, SourceText.ofString source, options) |> Async.RunSynchronously
        if result.ParseHadErrors then []
        else
            match result.ParseTree with
            | ParsedInput.ImplFile(ParsedImplFileInput(contents = modules)) ->
                modules
                |> List.collect (fun (SynModuleOrNamespace(decls = decls)) -> decls |> List.collect (bindings []))
                |> List.filter (snd >> isFunction)
                |> List.choose (fun (prefix, b) ->
                    match nameOf b with
                    | [] -> None
                    | name ->
                        let (SynBinding(expr = body)) = b
                        let r = b.RangeOfBindingWithRhs
                        Some { File = file
                               Name = String.concat "." (prefix @ name)
                               Line = r.StartLine
                               Cyclomatic = 1 + decisions body
                               Lines = r.EndLine - r.StartLine + 1
                               Coverage = None })
            | ParsedInput.SigFile _ -> []
