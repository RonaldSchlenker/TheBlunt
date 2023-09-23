﻿module TheBlunt

open System

type ParserFunction<'value, 'state> = Cursor -> 'state -> ParserResult<'value>

and [<Struct>] Cursor =
    { idx: int
      text: Str }

and Str = ReadOnlyMemory<char>

and [<Struct>] ParserResult<'out> =
    | POk of ok: ParserResultValue<'out>
    | PError of error: ParseError

and [<Struct>] ParserResultValue<'out> =
    { idx: int
      value: 'out }

and [<Struct>] ParseError =
    { idx: int
      message: string }

type [<Struct>] DocPos =
    { idx: int
      ln: int
      col: int }

type ForState<'i, 'v>(appendValue, getItems, addItem, appendStateItem) =
    member val Stop = false with get, set
    member _.AppendValue(value: 'v) : unit = appendValue value
    member _.Items : list<'i> = getItems ()
    member _.AddItem(item: 'i) : unit = addItem item
    member _.AppendStateItem() : unit = appendStateItem ()

#if USE_SINGLE_CASE_DU

type Parser<'value, 'state> = Parser of ParserFunction<'value, 'state>

[<AutoOpen>]
module ParserHandling =
    let inline mkParser parserFunction = Parser parserFunction
    let inline getParser (parser: Parser<_,_>) = let (Parser p) = parser in p

module Inline =
    type IfLambdaAttribute() = inherit System.Attribute()

#else

type Parser<'value, 'state> = ParserFunction<'value, 'state>

[<AutoOpen>]
module ParserHandling =
    let inline mkParser (parser: Parser<_,_>) = parser
    let inline getParser (parser: Parser<_,_>) = parser

module Inline =
    type IfLambdaAttribute = FSharp.Core.InlineIfLambdaAttribute

#endif

open System.Runtime.CompilerServices

[<Extension>]
type StringExtensions =
    [<Extension>] static member inline ValueEquals(s: Str, compareWith: string)
        = s.Span.SequenceEqual(compareWith.AsSpan())
    [<Extension>] static member inline ValueEquals(s: Str, compareWith: Str) 
        = s.Span.SequenceEqual(compareWith.Span)
    [<Extension>] static member inline ValueEquals(s: string, compareWith: Str) 
        = s.AsSpan().SequenceEqual(compareWith.Span)
    [<Extension>] static member inline ValueEquals(s: string, compareWith: string) 
        = String.Equals(s, compareWith)
        
    [<Extension>] static member ValueEqualsAt(this: Str, other: Str, idx: int) 
        = 
        idx + other.Length <= this.Length 
        && this.Slice(idx, other.Length).ValueEquals(other)
    [<Extension>] static member ValueEqualsAt(this: Str, other: string, idx: int) 
        = this.ValueEqualsAt(other.AsMemory(), idx)

module Str =
    let empty = "".AsMemory()

type Cursor with
    static member Create(text, idx) = { idx = idx; text = text }
    member c.CanGoto(idx: int) =
        // TODO: Should be: Only forward
        idx >= 0 && idx <= c.text.Length
    member c.CanWalk(steps: int) = c.CanGoto(c.idx + steps)
    member c.IsAtEnd = c.idx = c.text.Length
    member c.HasRest = c.idx < c.text.Length

// TODO: Perf: The parser combinators could track that, instead of computing it from scratch.
module DocPos =
    let create (index: int) (input: Str) =
        if index < 0 || index > input.Length then
            failwithf "Index %d is out of range of input string of length %d." index input.Length
        
        let lineStart, columnStart = 1, 1
        let rec findLineAndColumn (currIdx: int) (line: int) (column: int) =
            match currIdx = index with
            | true -> { idx = index; ln = line; col = column }
            | false ->
                let line, column =
                    if input.ValueEqualsAt("\n".AsMemory(), currIdx)
                    then line + 1, columnStart
                    else line, column + 1
                findLineAndColumn (currIdx + 1) line column
        findLineAndColumn 0 lineStart columnStart

    let ofInput (pi: Cursor) = create pi.idx pi.text

let hasConsumed lastIdx currIdx = lastIdx > currIdx

let inline bind ([<InlineIfLambda>] f: 'a -> Parser<_,_>) (parser: Parser<_,_>) =
    mkParser <| fun inp state ->
        match getParser parser inp state with
        | PError error -> PError error
        | POk pRes ->
            let fParser = getParser (f pRes.value)
            fParser { inp with idx = pRes.idx } state

let return' value =
    mkParser <| fun inp state -> 
        POk { idx = inp.idx; value = value }

let inline run (text: string) (parser: Parser<_,_>) =
    let text = text.AsMemory()
    match getParser parser { idx = 0; text = text } () with
    | POk res -> Ok res.value
    | PError error ->
        let docPos = DocPos.create error.idx text
        Error {| pos = docPos; message = error.message |}

type ParserBuilder() =
    member inline _.Bind(p, [<InlineIfLambda>] f) = bind f p
    member _.Return(value) = return' value
    member _.ReturnFrom(value) = value
    member _.Yield(value) = return' [value]
    member _.YieldFrom(p: Parser<_,_>) =
        mkParser <| fun inp state ->
            let pRes = getParser p inp state
            match pRes with
            | PError err -> PError err
            | POk pRes -> POk { idx = pRes.idx; value = [pRes.value] }
    member _.Zero() = return' ()
    member _.Delay(f) = f
    member _.Run(f) = 
        mkParser <| fun inp state ->
            getParser (f ()) inp state
    member _.Combine(p1, fp2) = 
        mkParser <| fun inp state ->
            let p2 = fp2 ()
            match getParser p1 inp state with
            | POk p1Res ->
                match getParser p2 { inp with idx = p1Res.idx } state with
                | POk p2Res ->
                    POk
                        { idx = p2Res.idx
                          value = List.append p1Res.value p2Res.value }
                | PError error -> PError error
            | PError error -> PError error
    member _.While(guard, body) =
        mkParser <| fun inp state ->
            let rec iter currResults currIdx =
                match guard () with
                | true ->
                    match getParser (body ()) { inp with idx = currIdx } state with
                    | PError error -> PError error
                    | POk res ->
                        if hasConsumed res.idx currIdx
                        then iter (List.append currResults res.value) res.idx
                        else POk { idx = currIdx; value = currResults }
                | false -> 
                    POk { idx = currIdx; value = currResults }
            iter [] inp.idx
    // Should that work similar to the ForParser overload (like "yield Item x")?
    member this.For(sequence: _ seq, body) =
        let enum = sequence.GetEnumerator()
        this.While(
            (fun _ -> enum.MoveNext()),
            (fun () -> body enum.Current))
    member _.For(loopParser, body: _ -> Parser<unit,_>) =
        mkParser <| fun inp state ->
            // TODO: This is hardcoced and specialized for Strings
            let forState =
                let sb = System.Text.StringBuilder()
                let items = ResizeArray<string>()
                let appendValue (value: string) = sb.Append(value) |> ignore
                let getItems () = [ yield! items ]
                let addItem item = items.Add(item) |> ignore
                let appendStateItem () = items.Add(sb.ToString()) |> ignore
                ForState(appendValue, getItems, addItem, appendStateItem)
            let rec iter currIdx =
                match getParser loopParser { inp with idx = currIdx } state with
                | PError err -> 
                    POk { idx = currIdx; value = forState.Items }
                | POk loopRes ->
                    let bodyP = body loopRes.value
                    match getParser bodyP { inp with idx = loopRes.idx } forState with
                    | PError err -> PError err
                    | POk bodyRes ->
                        let ok () = POk { idx = bodyRes.idx; value = forState.Items } // TODO: Perf
                        match forState.Stop || inp.IsAtEnd with
                        | true -> ok ()
                        | false ->
                            let continue' =
                                if hasConsumed bodyRes.idx currIdx
                                then Some bodyRes.idx
                                elif { inp with idx = bodyRes.idx }.CanWalk(1) 
                                then Some (bodyRes.idx + 1)
                                else None
                            match continue' with
                            | Some idx -> iter idx
                            | None -> ok ()
            iter inp.idx

let parse = ParserBuilder()

module For =
    let stop<'i,'v> =
        mkParser <| fun inp (state: ForState<'i,'v>) ->
            state.Stop <- true
            POk { idx = inp.idx; value = () }
    let appendString (s: string) =
        mkParser <| fun inp (state: ForState<_,_>) ->
            do state.AppendValue(s)
            POk { idx = inp.idx; value = () }
    let yieldItem (s: string) =
        mkParser <| fun inp (state: ForState<_,_>) ->
            do state.AddItem(s)
            POk { idx = inp.idx; value = () }
    let yieldState<'i,'v> =
        mkParser <| fun inp (state: ForState<'i,'v>) ->
            do state.AppendStateItem()
            POk { idx = inp.idx; value = () }
    let getState<'i,'v> =
        mkParser <| fun inp (state: ForState<'i,'v>) ->
            POk { idx = inp.idx; value = state }

let map proj (p: Parser<_,_>) =
    mkParser <| fun inp state ->
        match getParser p inp state with
        | PError error -> PError error
        | POk pRes -> POk { idx = pRes.idx; value = proj pRes.value }

let pignore (p: Parser<_,_>) =
    p |> map (fun _ -> ())

let pstr (s: string) =
    mkParser <| fun inp (state: unit) ->
        if inp.text.ValueEqualsAt(s, inp.idx)
        then POk { idx = inp.idx + s.Length; value = s }
        else PError { idx = inp.idx; message = $"Expected: '{s}'" }

let goto (idx: int) =
    mkParser <| fun inp (state: unit) ->
        if inp.CanGoto(idx) then 
            POk { idx = idx; value = () }
        else
            // TODO: this propably would be a fatal, most propably an unexpected error
            let msg = $"Index {idx} is out of range of string of length {inp.text.Length}."
            PError { idx = idx; message = msg }

let por a b =
    mkParser <| fun inp state ->
        match getParser a inp state with
        | POk res -> POk res
        | PError _ -> getParser b inp state
let ( <|> ) a b = por a b

let pchoose parsers = parsers |> List.reduce por

// TODO: sepBy
// TODO: skipN

//let puntil (until: Parser<_,_>) =
//    parse {
//    }

let panyChar =
    mkParser <| fun inp (state: unit) ->
        if inp.IsAtEnd
        then POk { idx = inp.idx; value = "" }
        else POk { idx = inp.idx + 1; value = inp.text.Slice(inp.idx, 1).ToString() }

let pend =
    mkParser <| fun inp (state: unit) ->
        if inp.idx = inp.text.Length - 1
        then POk { idx = inp.idx + 1; value = () }
        else PError { idx = inp.idx; message = "End of input." }

let pblank = pstr " "
// TODO: blankN

/// Parse at least n or more blanks.
let pblanks n =
    parse {
        for x in 1 .. n do
            yield! pstr " "
        for x in pstr " " do
            do! For.appendString x
    }

// let pSepByStr (p: Parser<_,_>) (sep: Parser<_,_>) =
//     parse {
//         for x
//     }

// TODO: passable reduce function / Zero for builder, etc.
// Name: Imparsible


module Tests =
    let r = pblank |> run "   "
    let r = pblank |> run " "
    let r = pblank |> run "x"
    let r = pblank |> run ""
    
    let r = pblanks 1 |> run "   xxx"
    let r = pblanks 1 |> run "  xxx"
    let r = pblanks 1 |> run " xxx"
    let r = pblanks 1 |> run "xxx"
    let r = pblanks 0 |> run "xxx"

    let r =
        parse {
            for x in panyChar do
                if x = "a" || x = "b" || x = "c" then
                    do! For.appendString x
                elif x = "X" then
                    do! For.stop
        }
        |> run "abcdeaXabb"

    
    /// Parse at least n or more blanks.
    let pblanksAlternative n =
        parse {
            for x in 1 .. n do
                yield! pstr " "
            for x in panyChar do
                do! For.appendString x
                do! For.yieldItem "kjlk"
            // yield! state
        }


    let r = pblanksAlternative 1 |> run "       xxx"
    let r = pblanksAlternative 1 |> run "   xxx"
    let r = pblanksAlternative 1 |> run " xxx"
    let r = pblanksAlternative 1 |> run "xxx"
    let r = pblanksAlternative 0 |> run "xxx"
