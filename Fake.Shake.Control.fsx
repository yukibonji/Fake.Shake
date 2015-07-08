#if INTERACTIVE
#r "packages/FAKE/tools/FakeLib.dll"
#r "packages/FsPickler/lib/net45/FsPickler.dll"
#load "Fake.Shake.Core.fsx"
#else
[<AutoOpen>]
module Fake.Shake.Control
#endif
open Fake
open Fake.Shake.Core
open Nessos.FsPickler

let binary = FsPickler.CreateBinary()

let bind (continuation : 'a -> Action<'b>) (expr : Action<'a>) : Action<'b> =
    fun state ->
        let state', result = (expr state)
        continuation (result.Force()) state'

let (>>=) expr continuation =
    bind continuation expr

let return' x =
    fun state ->
        state, lazy x

let map f act =
    bind (f >> return') act

let combine expr1 expr2 =
    expr1 |> bind (fun () -> expr2)

let tryWith expr handler =
    fun state ->
        try expr state
        with e -> (handler e) state

let tryFinally expr comp =
    fun state ->
        try expr state
        finally comp()

let rec skip key state =
    let maybeRule = State.rawFind state key
    match maybeRule with
    | None -> false
    | Some rule ->
        match state.OldResults |> Map.tryFind key with
        | Some old ->
            let deps = match state.Dependencies |> Map.tryFind key with None -> [] | Some ds -> ds
            rule.ValidStored key old
            && deps |> List.forall (fun dep -> skip dep state)
        | None -> false

let run<'a> key state =
    if skip key state then
        tracefn "Skipped %A, valid stored result" key
        let pickle = lazy state.OldResults.[key]
        let (lazyResult : Lazy<'a>) = lazy (state.OldResults.[key] |> (binary.UnPickle))
        { state with Results = state.Results |> Map.add key pickle }, lazyResult
    else
        let rule = State.find state key
        let (state', result) = rule.Action key (state |> State.clearDeps key)
        let pickled =
            lazy
                result.Force()
                |> binary.Pickle
        { state' with Results = state'.Results |> Map.add key pickled }, result

let require<'a> key : Action<'a> =
    fun state ->
        tracefn "%A required via stack %A" key state.Stack
        let state =
            state
            |> State.push key
        let state, result =
            match state.Results |> Map.tryFind key with
            | Some r -> state, lazy (r.Force() |> binary.UnPickle)
            | None -> run key state
        State.pop state, result

let requires<'a> keys : Action<'a list> =
    let rec inner keys (values : Action<'a list>) =
        match keys with
        | key::t ->
            values
            >>= (fun values' ->
                    require<'a> key
                    >>= fun (value' : 'a) ->
                            return' <| value'::values')
            |> inner t
        | [] -> values |> map (List.rev)
    inner keys (return' List.empty<'a>)

let need key : Action<unit> =
    require<ContentHash> key
    |> map ignore

let needs keys : Action<unit> =
    requires<ContentHash> keys
    |> map ignore

type ActionBuilder () =
    member __.Bind(expr, cont) =
        bind cont expr
    member __.Return x =
        return' x
    member __.Zero () =
        return' ()
    member __.ReturnFrom x =
        x
    member this.Delay (cont) =
        this.Bind (this.Return (), cont)
    member __.Combine (expr1, expr2) =
        combine expr1 expr2
    member this.Yield x =
        this.Return x
    member __.TryFinally (expr, compensation) =
        tryFinally expr compensation
    member __.TryWith (expr, handler) =
        tryWith expr handler
    member this.Using (res : #System.IDisposable, body) =
        this.TryFinally (body res, (fun () ->
            match res with
            | null -> ()
            | disp -> disp.Dispose()))
    member this.While (guard, expr) =
        match guard () with
        | true -> this.Bind(expr, fun () -> this.While (guard, expr))
        | _ -> this.Zero ()
    member this.For (sequence : seq<'a>, body) =
        this.Using (sequence.GetEnumerator(), fun enum ->
            this.While(enum.MoveNext, this.Delay(fun () -> body enum.Current)))

let action = ActionBuilder()