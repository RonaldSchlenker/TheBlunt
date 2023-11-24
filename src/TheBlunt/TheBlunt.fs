﻿module TheBlunt

open System
open System.Runtime.CompilerServices

type Str =
    #if !FABLE_COMPILER && NETSTANDARD2_1_OR_GREATER
    System.ReadOnlySpan<char>
    #else
    System.String
    #endif

[<Extension>]
type StringExtensions =

    #if !FABLE_COMPILER && NETSTANDARD2_1_OR_GREATER
    [<Extension>] 
    static member inline StringEquals(s: Str, compareWith: string) = 
        s.SequenceEqual(compareWith.AsSpan())
    [<Extension>] 
    static member inline StringEquals(s: Str, compareWith: Str) =
        s.SequenceEqual(compareWith)
    [<Extension>]
    static member inline StringEquals(s: string, compareWith: Str)  =
        s.AsSpan().SequenceEqual(compareWith)
    #endif
    [<Extension>]
    static member inline StringEquals(s: string, compareWith: string) = 
        String.Equals(s, compareWith)

    #if !FABLE_COMPILER && NETSTANDARD2_1_OR_GREATER
    [<Extension>]
    static member StringStartsWithAt(this: Str, other: Str, idx: int) =
        idx + other.Length <= this.Length
        && this.Slice(idx, other.Length).StringEquals(other)
    [<Extension>] 
    static member StringStartsWithAt(this: Str, other: string, idx: int) =
        this.StringStartsWithAt(other.AsSpan(), idx)
    [<Extension>]
    static member StringStartsWithAt(this: string, other: string, idx: int) =
        this.AsSpan().StringStartsWithAt(other.AsSpan(), idx)
    #else
    [<Extension>]
    static member StringStartsWithAt(this: string, other: string, idx: int) =
        this.Substring(idx).StartsWith(other)
    #endif

    #if !FABLE_COMPILER && NETSTANDARD2_1_OR_GREATER
    [<Extension>]
    static member Slice(this: string, start: int) =
        this.AsSpan().Slice(start)
    #else
    [<Extension>]
    static member Slice(this: string, start: int) =
        this.Substring(start)
    #endif


// -----------------------------------------------------------------------------------------------
// BEGIN :)
// -----------------------------------------------------------------------------------------------


type Parser<'value> = Cursor -> ParserResult<'value>

and [<Struct>] Cursor =
    { original: string
      idx: int }

and [<Struct>] ParserResult<'out> =
    | POk of ok: PVal<'out>
    | PError of error: ParseError

and [<Struct>] PVal<'out> =
    { range: Range
      result: 'out }

and [<Struct>] ParseError =
    { idx: int
      message: string }

and [<Struct>] Range = 
    { startIdx: int
      endIdx: int }

type [<Struct>] DocPos =
    { idx: int
      ln: int
      col: int }

let inline mkParser ([<InlineIfLambda>] parser: Parser<_>) = parser
let inline getParser ([<InlineIfLambda>] parser: Parser<_>) = parser

type Cursor with
    member c.CanGoto(idx: int) =
        // TODO: Should be: Only forward
        idx >= c.idx && idx <= c.original.Length
    member c.CanWalkFwd(steps: int) = c.CanGoto(c.idx + steps)
    member c.IsAtEnd = c.idx = c.original.Length
    member c.HasRest = c.idx < c.original.Length
    member c.Rest : Str = c.original.Slice(c.idx)
    member c.StartsWith(s: string) = c.Rest.StringStartsWithAt(s, 0)
    member c.Goto(idx: int) =
        if not (c.CanGoto(idx)) then
            failwithf "Index %d is out of range of string of length %d." idx c.original.Length
        { idx = idx; original = c.original }
    member c.WalkFwd(steps: int) = c.Goto(c.idx + steps)
    member c.MoveNext() = c.WalkFwd(1)

module Range =
    let inline create startIdx endIdx = { startIdx = startIdx; endIdx = endIdx }
    let zero = { startIdx = 0; endIdx = 0 }
    let inline add r1 r2 = { startIdx = r1.startIdx; endIdx = r2.endIdx }
    let inline merge ranges = ranges |> List.reduce add

module POk =
    let inline createFromRange range result =
        POk { range = range; result = result }
    let inline create startIdx endIdx result =
        createFromRange (Range.create startIdx endIdx) result

module PError =
    let inline create (idx: int) (message: string) =
        PError { idx = idx; message = message }

module PVal =
    let inline map ([<InlineIfLambda>] proj) (pval: PVal<'a>) =
        { range = pval.range; result = proj pval.result }
    let ranges (pvals: PVal<'a> list) = 
        pvals |> List.map (fun x -> x.range) |> Range.merge
    let reduce (pvals: PVal<'a> list) reducer = 
        let ranges = ranges pvals
        let result = pvals |> List.map (fun x -> x.result) |> List.reduce reducer
        { range = ranges; result = result }

// TODO: Perf: The parser combinators could track that, instead of computing it from scratch.
module DocPos =
    let create (index: int) (input: string) =
        if index < 0 || index > input.Length then
            failwithf "Index %d is out of range of input string of length %d." index input.Length
        let lineStart = 1
        let columnStart = 1
        let mutable currIdx = 0
        let mutable line = lineStart
        let mutable column = columnStart
        while currIdx <> index do
            let isLineBreak = input.StringStartsWithAt("\n", currIdx)
            if isLineBreak then
                line <- line + 1
                column <- columnStart
            else
                column <- column + 1
            currIdx <- currIdx + 1
        { idx = index; ln = line; col = column }
    let ofInput (pi: Cursor) = create pi.idx pi.original

module Cursor =
    let hasRemainingChars n =
        fun (inp: Cursor) ->
            if not (inp.CanWalkFwd n)
            then PError.create inp.idx (sprintf "Expected %d more characters." n)
            else POk.create inp.idx inp.idx ()
    let inline notAtEnd cursor = 
        hasRemainingChars 1 cursor

let inline pwhen pred ([<InlineIfLambda>] p) =
    mkParser <| fun inp ->
        match pred inp with
        | PError err -> PError.create inp.idx err.message
        | POk _ -> getParser p inp

let inline mkParserWhen pred ([<InlineIfLambda>] pf) =
    pwhen pred <| mkParser pf

let inline bind ([<InlineIfLambda>] f) ([<InlineIfLambda>] parser) =
    mkParser <| fun inp ->
        match getParser parser inp with
        | PError error -> PError error
        | POk pRes ->
            let fParser = getParser (f pRes)
            fParser (inp.Goto(pRes.range.endIdx))

type ParserBuilder() =
    member inline _.Bind([<InlineIfLambda>] p, [<InlineIfLambda>] f) =
        bind f p
    member _.Return(pval: PVal<_>) =
        mkParser (fun inp -> POk pval)
    member _.Return(err: ParseError) =
        mkParser (fun inp -> PError err)

let parse = ParserBuilder()

let pseq (s: _ seq) =
    let enum = s.GetEnumerator()
    mkParser (fun inp ->
        if enum.MoveNext()
        then POk.create inp.idx inp.idx enum.Current
        else PError.create inp.idx "No more elements in sequence."
    )

let inline run (text: string) ([<InlineIfLambda>] parser) =
    getParser parser { idx = 0; original = text }

let inline map ([<InlineIfLambda>] proj) ([<InlineIfLambda>] p) =
    mkParser (fun inp ->
        match getParser p inp with
        | PError error -> PError error
        | POk pres -> POk { range = pres.range; result = proj pres.result }
    )

let inline mapPVal ([<InlineIfLambda>] proj) ([<InlineIfLambda>] p) =
    mkParser (fun inp ->
        match getParser p inp with
        | PError error -> PError error
        | POk pres -> POk { range = pres.range; result = proj pres }
    )

let inline pignore ([<InlineIfLambda>] p) =
    map (fun _ -> ()) p

let inline pattempt ([<InlineIfLambda>] p) =
    mkParser (fun inp ->
        match getParser p inp with
        | POk res -> POk.createFromRange res.range (Some res)
        | PError err -> PError err
    )

let inline ptry ([<InlineIfLambda>] p) =
    mkParser (fun inp ->
        match getParser p inp with
        | POk res -> POk.createFromRange res.range (Some res)
        | PError err -> POk.createFromRange (Range.create inp.idx inp.idx) None
    )

let inline pisOk ([<InlineIfLambda>] p) = 
    mkParser (fun inp ->
        match getParser p inp with
        | POk res -> POk.createFromRange res.range true
        | PError err -> POk.create inp.idx inp.idx false
    )

let inline pisErr ([<InlineIfLambda>] p) =
    pisOk p |> map not

// TODO: A strange thing is this
let inline pnot ([<InlineIfLambda>] p) =
    mkParser (fun inp ->
        match getParser (pattempt p) inp with
        | POk _ -> PError.create inp.idx "Unexpected." // TODO
        | PError _ -> POk.create inp.idx inp.idx ()
    )

let pstr (s: string) =
    mkParser (fun inp ->
        if inp.StartsWith(s)
        then POk.create inp.idx (inp.idx + s.Length) s
        else PError.create inp.idx (sprintf "Expected: '%s'" s)
    )
let ( ~% ) = pstr

let pgoto (idx: int) =
    mkParser (fun inp ->
        if inp.CanGoto(idx) then 
            POk.create inp.idx idx ()
        else
            // TODO: this propably would be a fatal, most propably an unexpected error
            let msg = sprintf "Index %d is out of range of string of length %d." idx inp.original.Length
            PError.create idx msg
    )

let inline orThen ([<InlineIfLambda>] pa) ([<InlineIfLambda>] pb) =
    mkParser (fun inp ->
        match getParser pa inp with
        | POk res -> POk res
        | PError _ -> getParser pb inp
    )
let ( <|> ) = orThen

let inline andThen ([<InlineIfLambda>] pa) ([<InlineIfLambda>] pb) =
    mkParser (fun inp ->
        match getParser pa inp with
        | POk ares ->
            match getParser pb (inp.Goto ares.range.endIdx) with
            | POk bres -> POk.create inp.idx bres.range.endIdx (ares.result, bres.result)
            | PError error -> PError error
        | PError error -> PError error
    )
// type AndThen = AndThen with
//     static member inline ($) (AndThen, x: (Parser<_> * Parser<_>)) =
//         let a,b = x
//         andThen a b
// let inline ( <&> ) a b = (($) AndThen) (a, b)
let inline ( .>. ) ([<InlineIfLambda>] pa) ([<InlineIfLambda>] pb) = andThen pa pb
let inline ( .>> ) ([<InlineIfLambda>] pa) ([<InlineIfLambda>] pb) = andThen pa pb |> map fst
let inline ( >>. ) ([<InlineIfLambda>] pa) ([<InlineIfLambda>] pb) = andThen pa pb |> map snd

let firstOf parsers = parsers |> List.reduce orThen

let inline manyN minOccurances ([<InlineIfLambda>] p: Parser<_>) =
    mkParser (fun inp ->
        let mutable currIdx = inp.idx
        let mutable run = true
        let mutable iterations = 0
        let res =
            [ while run do
                match getParser p (inp.Goto(currIdx)) with
                | POk res ->
                    yield res
                    do currIdx <- res.range.endIdx
                    do
                        if inp.idx = currIdx
                        then run <- false
                        else iterations <- iterations + 1
                | PError _ ->
                    do run <- false
            ]
        if iterations < minOccurances 
        then PError.create currIdx $"Expected {minOccurances} occurances, but got {iterations}."
        else POk.create inp.idx currIdx res
    )

let inline many ([<InlineIfLambda>] p) = manyN 0 p

let inline many1 ([<InlineIfLambda>] p) =
    parse {
        let! res = many p
        match res.result with
        | [] -> return { idx = res.range.startIdx; message = "Expected at least one element." }
        | _ -> return res
    }

// TODO: skipN

let anyChar =
    mkParserWhen Cursor.notAtEnd (fun inp ->
        POk.create inp.idx (inp.idx + 1) (inp.Rest.[0].ToString())
    )

let inline noRanges ([<InlineIfLambda>] p: Parser<PVal<'a> list>) =
    map (fun pvals -> pvals |> List.map (fun x -> x.result)) p

let inline pconcat ([<InlineIfLambda>] p: Parser<PVal<string> list>) =
    map (fun pvals -> pvals |> List.map (fun x -> x.result) |> String.concat "") p

// TODO: anyCharExcept(c,p)

let eoi =
    mkParser (fun inp ->
        if inp.IsAtEnd
        then POk.create inp.idx inp.idx ()
        else PError.create inp.idx "Expected end of input."
    )

let blank = pstr " "
let blanks = many blank |> pconcat
let blanks1 = many1 blank |> pconcat

let inline pstringUntil ([<InlineIfLambda>] puntil) =
    mkParser (fun inp ->
        let rec iter currIdx =
            match getParser (pattempt puntil) (inp.Goto currIdx) with
            | POk _ -> POk.create inp.idx currIdx (inp.original.Substring(inp.idx, currIdx - inp.idx))
            | PError _ ->
                if not (inp.CanGoto(currIdx + 1))
                then PError.create currIdx "End of input."
                else iter (currIdx + 1)
        iter inp.idx
    )

// TODO: make clear: Parsers that

let many1Str2 p1 p2 =
    parse {
        let! r1 = p1
        let! r2 = many p2 |> pconcat
        return
            {
                range = Range.add r1.range r2.range
                result = r1.result + r2.result
            }
    }

let inline setErrorMessage msg ([<InlineIfLambda>] p) =
    mkParser (fun inp ->
        match getParser p inp with
        | POk _ as res -> res
        | PError err -> PError { err with message = msg }
    )

let inline many1Str ([<InlineIfLambda>] p) = many1Str2 p p

let pchar predicate errMsg =
    mkParserWhen Cursor.notAtEnd <| fun inp ->
        let c = inp.Rest.[0]
        if predicate c
        then POk.create inp.idx (inp.idx + 1) (string c)
        else PError.create inp.idx (errMsg c)

let pchar1 expectedChar =
    pchar (fun c -> c = expectedChar) (fun c -> $"Expected '{expectedChar}', got '{c}'.")

let letter =
    pchar (Char.IsLetter) (sprintf "Expected letter, but got '%c'.")

let digit =
    pchar (Char.IsDigit) (sprintf "Expected letter, but got '%c'.")

let inline notFollowedBy ([<InlineIfLambda>] p) suffix =
    parse {
        let! x = p
        let! _ = pnot (pstr suffix)
        return x
    }

let inline psepBy1 ([<InlineIfLambda>] psep) ([<InlineIfLambda>] pelem: Parser<_>) =
    parse {
        let! x = pelem
        let! xs = many (psep >>. pelem)
        return 
            {
                range = Range.add x.range xs.range
                result = x :: xs.result
            }
    }

let pchoice parsers =
    mkParser <| fun inp ->
        let rec iter parsers =
            match parsers with
            | [] -> PError.create inp.idx "No more parsers to try." // TODO: Better: Expected ... or ... or ...; collect "err"
            | p::ps ->
                match getParser p inp with
                | POk _ as res -> res
                | PError err -> iter ps
        iter parsers
