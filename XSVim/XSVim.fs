﻿namespace XSVim

open System
open MonoDevelop.Ide.Editor
open MonoDevelop.Ide.Editor.Extension
open Mono.TextEditor
open MonoDevelop.Core

type BeforeOrAfter = Before | After

type VimMode =
    | NormalMode
    | VisualMode
    | VisualBlockMode
    | VisualLineMode
    | InsertMode

type CommandType =
    | Move
    | Visual
    | Yank
    | Put of BeforeOrAfter
    | Delete
    | BlockInsert
    | Change
    | Go
    | SwitchMode of VimMode
    | Undo
    | InsertLine of BeforeOrAfter
    | RepeatLastAction
    | ResetKeys
    | DoNothing

type TextObject =
    | Character
    | AWord
    | InnerWord
    | AWORD
    | InnerWORD
    | ASentence
    | InnerSentence
    | AParagraph
    | InnerParagraph
    | ABlock of string * string
    | InnerBlock of string * string
    | WholeLine
    | WholeLineIncludingDelimiter
    | LastLine
    // motions
    | Up
    | Down
    | Left
    | Right
    | RightIncludingDelimiter
    | EnsureCursorBeforeDelimiter
    | FirstNonWhitespace
    | StartOfLine
    | StartOfDocument
    | EndOfLine
    | EndOfLineIncludingDelimiter
    | ToCharInclusive of string
    | ToCharInclusiveBackwards of string
    | ToCharExclusive of string
    | ToCharExclusiveBackwards of string
    | WordForwards
    | WORDForwards
    | WordBackwards
    | WORDBackwards
    | ForwardToEndOfWord
    | ForwardToEndOfWORD
    | BackwardToEndOfWord
    | BackwardToEndOfWORD
    | Nothing
    | HalfPageUp
    | HalfPageDown
    | PageUp
    | PageDown
    | CurrentLocation
    | Selection
    | SelectionStart

type VimAction = {
    repeat: int
    commandType: CommandType
    textObject: TextObject
}

type VimState = {
    keys: string list
    mode: VimMode
    visualStartOffset: int
    findCharCommand: VimAction option // f,F,t or T command to be repeated with ;
    lastAction: VimAction list // used by . command to repeat the last action
}

module VimHelpers =
    let findCharForwardsOnLine (editor:TextEditorData) (line:DocumentLine) character =
        let ch = Char.Parse character
        seq { editor.Caret.Offset+1 .. line.EndOffset }
        |> Seq.tryFind(fun index -> editor.Text.[index] = ch)

    let findCharBackwardsOnLine (editor:TextEditorData) (line:DocumentLine) character =
        let ch = Char.Parse character
        seq { editor.Caret.Offset-1 .. -1 .. line.Offset }
        |> Seq.tryFind(fun index -> editor.Text.[index] = ch)

    let findCharForwards (editor:TextEditorData) character =
        let ch = Char.Parse character
        seq { editor.Caret.Offset+1 .. editor.Text.Length }
        |> Seq.tryFind(fun index -> editor.Text.[index] = ch)

    let findCharBackwards (editor:TextEditorData) character =
        let ch = Char.Parse character
        seq { editor.Caret.Offset .. -1 .. 0 }
        |> Seq.tryFind(fun index -> editor.Text.[index] = ch)

    let findCharRange (editor:TextEditorData) startChar endChar =
        findCharBackwards editor startChar, findCharForwards editor endChar

    let findWordForwards (editor:TextEditorData) =
        let findFromNonLetterChar index =
            match editor.Text.[index] with
            | ' ' ->
                seq { index+1 .. editor.Text.Length } 
                |> Seq.tryFind(fun index -> editor.Text.[index] <> ' ')
            | _ -> Some index

        if not (Char.IsLetterOrDigit editor.Text.[editor.Caret.Offset]) && Char.IsLetterOrDigit editor.Text.[editor.Caret.Offset + 1] then 
            editor.Caret.Offset + 1 |> Some
        else
            seq { editor.Caret.Offset+1 .. editor.Text.Length }
            |> Seq.tryFind(fun index -> not (Char.IsLetterOrDigit editor.Text.[index]))
            |> Option.bind findFromNonLetterChar

    let getVisibleLineCount (editor:TextEditorData) =
        let topVisibleLine = ((editor.VAdjustment.Value / editor.LineHeight) |> int) + 1
        let bottomVisibleLine =
            Math.Min(editor.LineCount - 1,
                topVisibleLine + ((editor.VAdjustment.PageSize / editor.LineHeight) |> int))
        bottomVisibleLine - topVisibleLine

    let getRange (vimState:VimState) (editor:TextEditorData) motion =
        let line = editor.GetLine editor.Caret.Line
        match motion with
        | Right -> 
            let line = editor.GetLine editor.Caret.Line
            editor.Caret.Offset, if editor.Caret.Column < line.Length then editor.Caret.Offset + 1 else editor.Caret.Offset
        | RightIncludingDelimiter -> 
            let line = editor.GetLine editor.Caret.Line
            editor.Caret.Offset, if editor.Caret.Column < line.LengthIncludingDelimiter then editor.Caret.Offset + 1 else editor.Caret.Offset
        | EnsureCursorBeforeDelimiter -> 
            let line = editor.GetLine editor.Caret.Line
            editor.Caret.Offset, if editor.Caret.Column < line.Length then editor.Caret.Offset else editor.Caret.Offset - 1
        | Left -> editor.Caret.Offset, if editor.Caret.Column > DocumentLocation.MinColumn then editor.Caret.Offset - 1 else editor.Caret.Offset
        | Up ->
            editor.Caret.Offset,
            if editor.Caret.Line > DocumentLocation.MinLine then
                let visualLine = editor.LogicalToVisualLine(editor.Caret.Line)
                let lineNumber = editor.VisualToLogicalLine(visualLine - 1)
                editor.LocationToOffset (new DocumentLocation(lineNumber, editor.Caret.Column))
            else
                editor.Caret.Offset
        | Down ->
            editor.Caret.Offset,
            if editor.Caret.Line < editor.Document.LineCount then
                let visualLine = editor.LogicalToVisualLine(editor.Caret.Line)
                let lineNumber = editor.VisualToLogicalLine(visualLine + 1)
                editor.LocationToOffset (new DocumentLocation(lineNumber, editor.Caret.Column))
            else
                editor.Caret.Offset
        | EndOfLine -> editor.Caret.Offset, line.EndOffset
        | EndOfLineIncludingDelimiter -> editor.Caret.Offset, line.EndOffsetIncludingDelimiter
        | StartOfLine -> editor.Caret.Offset, line.Offset
        | StartOfDocument -> editor.Caret.Offset, 0
        | FirstNonWhitespace -> editor.Caret.Offset, line.Offset + editor.GetLineIndent(editor.Caret.Line).Length
        | WholeLine -> line.Offset, line.EndOffset
        | WholeLineIncludingDelimiter -> line.Offset, line.EndOffsetIncludingDelimiter
        | LastLine -> 
            let lastLine = editor.GetLine editor.Document.LineCount
            editor.Caret.Offset, lastLine.Offset
        | ToCharInclusiveBackwards c ->
            match findCharBackwardsOnLine editor line c with
            | Some index -> editor.Caret.Offset, index
            | None -> editor.Caret.Offset, editor.Caret.Offset
        | ToCharExclusiveBackwards c ->
            match findCharBackwardsOnLine editor line c with
            | Some index -> editor.Caret.Offset, index+1
            | None -> editor.Caret.Offset, editor.Caret.Offset
        | ToCharInclusive c ->
            match findCharForwardsOnLine editor line c with
            | Some index -> editor.Caret.Offset, index
            | None -> editor.Caret.Offset, editor.Caret.Offset
        | ToCharExclusive c ->
            match findCharForwardsOnLine editor line c with
            | Some index -> editor.Caret.Offset, index-1
            | None -> editor.Caret.Offset, editor.Caret.Offset
        | InnerBlock (startChar, endChar) ->
            match findCharRange editor startChar endChar with
            | Some start, Some finish -> start+1, finish
            | _, _ -> editor.Caret.Offset, editor.Caret.Offset
        | ABlock (startChar, endChar) ->
            match findCharRange editor startChar endChar with
            | Some start, Some finish when finish < editor.Text.Length -> start, finish+1
            | _, _ -> editor.Caret.Offset, editor.Caret.Offset
        | WordForwards -> 
            match findWordForwards editor with
            | Some index -> editor.Caret.Offset, index
            | None -> editor.Caret.Offset, editor.Caret.Offset
        | WordBackwards -> editor.Caret.Offset, editor.FindPrevWordOffset editor.Caret.Offset
        | ForwardToEndOfWord ->
            let endOfWord = editor.FindCurrentWordEnd (editor.Caret.Offset+1) - 1
            let endOfWord = 
                if editor.Text.[endOfWord] = ' ' then editor.FindCurrentWordEnd (endOfWord+1) - 1 else endOfWord
            editor.Caret.Offset, endOfWord
        | BackwardToEndOfWord -> editor.Caret.Offset, editor.FindPrevWordOffset editor.Caret.Offset |> editor.FindCurrentWordEnd
        | HalfPageUp -> 
            let visibleLineCount = getVisibleLineCount editor
            let halfwayUp = Math.Max(1, editor.Caret.Line - visibleLineCount / 2)
            editor.Caret.Offset, editor.GetLine(halfwayUp).Offset
        | HalfPageDown -> 
            let visibleLineCount = getVisibleLineCount editor
            let halfwayDown = Math.Min(editor.Document.LineCount, editor.Caret.Line + visibleLineCount / 2)
            editor.Caret.Offset, editor.GetLine(halfwayDown).Offset
        | PageUp -> 
            let visibleLineCount = getVisibleLineCount editor
            let pageUp = Math.Max(1, editor.Caret.Line - visibleLineCount)
            editor.Caret.Offset, editor.GetLine(pageUp).Offset
        | PageDown -> 
            let visibleLineCount = getVisibleLineCount editor
            let pageDown = Math.Min(editor.Document.LineCount, editor.Caret.Line + visibleLineCount)
            editor.Caret.Offset, editor.GetLine(pageDown).Offset
        | CurrentLocation -> editor.Caret.Offset, editor.Caret.Offset+1
        | Selection -> 
            let selection = editor.Selections |> Seq.head
            let lead = selection.GetLeadOffset editor
            let anchor = selection.GetAnchorOffset editor
            Math.Min(lead, anchor), Math.Max(lead, anchor)
        | SelectionStart -> editor.Caret.Offset, vimState.visualStartOffset
        | _ -> editor.Caret.Offset, editor.Caret.Offset

type XSVim() =
    inherit TextEditorExtension()

    let (|VisualModes|NonVisualMode|) mode =
        match mode with
        | VisualMode | VisualLineMode | VisualBlockMode -> VisualModes
        | _ -> NonVisualMode

    let setSelection vimState (editor:TextEditorData) (command:VimAction) (start:int) finish =
        match vimState.mode with
        | NormalMode ->
            editor.SetSelection(start, finish)
        | VisualMode ->
            let start, finish =
                if finish < vimState.visualStartOffset then
                    finish, vimState.visualStartOffset + 1
                else
                    vimState.visualStartOffset, finish + if command.textObject = EndOfLine then 0 else 1
            editor.SetSelection(start, finish)
        | VisualBlockMode ->
            let selectionStartLocation = editor.OffsetToLocation vimState.visualStartOffset
            let leftColumn, rightColumn =
                if editor.Caret.Column < selectionStartLocation.Column then
                    editor.Caret.Column, selectionStartLocation.Column+1
                else
                    selectionStartLocation.Column, editor.Caret.Column+1
            let topLine = Math.Min(selectionStartLocation.Line, editor.Caret.Line)
            let bottomLine = Math.Max(selectionStartLocation.Line, editor.Caret.Line)
            editor.MainSelection <-
                new Selection(new DocumentLocation (topLine, leftColumn), new DocumentLocation (bottomLine, rightColumn), SelectionMode.Block)
        | VisualLineMode ->
            let startPos = Math.Min(finish, vimState.visualStartOffset)
            let endPos = Math.Max(finish, vimState.visualStartOffset)
            let startLine = editor.GetLineByOffset startPos
            let endLine = editor.GetLineByOffset endPos
            editor.SetSelection(startLine.Offset, endLine.EndOffsetIncludingDelimiter)
        | _ -> ()

    let runCommand vimState editor command =
        for i in [1..command.repeat] do
            let start, finish = VimHelpers.getRange vimState editor command.textObject
            match command.commandType with
            | Move -> 
                editor.Caret.Offset <- finish
                match vimState.mode with
                | VisualModes -> setSelection vimState editor command start finish
                | _ -> ()
            | Delete ->
                let finish =
                    match command.textObject with
                    | ForwardToEndOfWord -> finish + 1
                    | _ -> finish
                if command.textObject <> Selection then
                    setSelection vimState editor command start finish
                ClipboardActions.Cut editor
            | Yank ->
                let finish =
                    match command.textObject with
                    | ForwardToEndOfWord -> finish + 1
                    | _ -> finish
                setSelection vimState editor command start finish
                ClipboardActions.Copy editor
                editor.ClearSelection()
            | Put Before ->
                let clipboard = ClipboardActions.GetClipboardContent()
                if clipboard.EndsWith "\n" then
                    editor.Caret.Offset <- editor.GetLine(editor.Caret.Line).Offset
                    ClipboardActions.Paste editor
                    CaretMoveActions.Up editor
                else
                    ClipboardActions.Paste editor
            | Put After ->
                let clipboard = ClipboardActions.GetClipboardContent()
                if clipboard.EndsWith "\n" then
                    editor.Caret.Offset <- editor.GetLine(editor.Caret.Line).EndOffset+1
                    ClipboardActions.Paste editor
                    CaretMoveActions.Up editor
                else
                    editor.Caret.Offset <- editor.Caret.Offset + 1
                    ClipboardActions.Paste editor
                    editor.Caret.Offset <- editor.Caret.Offset - 1
            | Visual -> editor.SetSelection(start, finish)
            | Undo -> MiscActions.Undo editor
            | InsertLine Before -> MiscActions.InsertNewLineAtEnd editor
            | InsertLine After -> editor.Caret.Column <- 1; MiscActions.InsertNewLine editor; CaretMoveActions.Up editor
            | _ -> ()

        match command.commandType with
        | ResetKeys -> { vimState with keys = [] }
        | BlockInsert ->
            editor.Caret.Mode <- CaretMode.Insert
            editor.Caret.PreserveSelection <- false
            let selectionStartLocation = editor.OffsetToLocation vimState.visualStartOffset
            let topLine = Math.Min(selectionStartLocation.Line, editor.Caret.Line)
            let bottomLine = Math.Max(selectionStartLocation.Line, editor.Caret.Line)
            editor.Caret.Column <- Math.Min(editor.Caret.Column, selectionStartLocation.Column)
            editor.MainSelection <-
                new Selection(new DocumentLocation (topLine, selectionStartLocation.Column),new DocumentLocation (bottomLine, selectionStartLocation.Column), SelectionMode.Block)
            { vimState with mode = InsertMode; keys = [] }
        | SwitchMode mode ->
            match mode with
            | NormalMode -> 
                editor.Caret.Mode <- CaretMode.Block
                editor.Caret.PreserveSelection <- false
                editor.ClearSelection()
                { vimState with mode = mode }
            | VisualMode | VisualLineMode | VisualBlockMode ->
                editor.SelectionMode <- if mode = VisualBlockMode then SelectionMode.Block else SelectionMode.Normal
                editor.Caret.Mode <- CaretMode.Block
                editor.Caret.PreserveSelection <- true
                let start, finish = VimHelpers.getRange vimState editor command.textObject
                let newState = { vimState with mode = mode; visualStartOffset = editor.Caret.Offset }
                setSelection newState editor command start finish
                newState
            | InsertMode ->
                editor.Caret.Mode <- CaretMode.Insert
                editor.Caret.PreserveSelection <- false
                { vimState with mode = mode; keys = [] }
        | _ -> vimState

    let (|Digit|_|) character =
        if character > "0" && character < "9" then
            Some (Convert.ToInt32 character)
        else
            None

    let (|OneToNine|_|) character =
        if character > "1" && character < "9" then
            Some (Convert.ToInt32 character)
        else
            None

    let (|BlockDelimiter|_|) character =
        let pairs =
            [ 
                "[", ("[", "]")
                "]", ("[", "]")
                "(", ("(", ")")
                ")", ("(", ")")
                "{", ("{", "}")
                "}", ("{", "}")
                "<", ("<", ">")
                ">", ("<", ">")
                "\"", ("\"", "\"")
                "'", ("'", "'")
            ] |> dict
        if pairs.ContainsKey character then
            Some pairs.[character]
        else
            None

    let (|Movement|_|) character =
        match character with
        | "h" -> Some Left
        | "j" -> Some Down
        | "k" -> Some Up
        | "l" -> Some Right
        | "$" -> Some EndOfLine
        | "^" -> Some StartOfLine
        | "0" -> Some StartOfLine
        | "_" -> Some FirstNonWhitespace
        | "w" -> Some WordForwards
        | "b" -> Some WordBackwards
        | "e" -> Some ForwardToEndOfWord
        | "E" -> Some BackwardToEndOfWord
        | "G" -> Some LastLine
        | "<C-d>" -> Some HalfPageDown
        | "<C-u>" -> Some HalfPageUp
        | "<C-f>" -> Some PageDown
        | "<C-b>" -> Some PageUp
        | _ -> None

    let (|FindChar|_|) character =
        match character with
        | "f" -> Some ToCharInclusive
        | "F" -> Some ToCharInclusiveBackwards
        | "t" -> Some ToCharExclusive
        | "T" -> Some ToCharExclusiveBackwards
        | _ -> None

    let (|Action|_|) character =
        match character with
        | "d" -> Some Delete
        | "c" -> Some Change
        | "v" -> Some Visual
        | "y" -> Some Yank
        | "g" -> Some Go
        | _ -> None

    let (|ModeChange|_|) character =
        match character with
        | "i" -> Some InsertMode
        | "v" -> Some VisualMode
        | "<C-v>" -> Some VisualBlockMode
        | "V" -> Some VisualLineMode
        | _ -> None

    let (|Escape|_|) character =
        match character with
        | "<esc>" -> Some Escape
        | "<C-c>" -> Some Escape
        | _ -> None

    let (|Keys|_|) (keys:string) =
        keys |> Seq.map(fun c -> c |> string) |> List.ofSeq |> Some

    let (|NotInsertMode|InsertModeOn|) mode =
        if mode = InsertMode then InsertModeOn else NotInsertMode

    let getCommand repeat commandType textObject =
        { repeat=repeat; commandType=commandType; textObject=textObject }

    let wait = [ getCommand 1 DoNothing Nothing ]

    let parseKeys (state:VimState) =
        let keyList = state.keys
        let multiplier, keyList =
            match keyList with
            // d2w -> 2, dw
            | c :: OneToNine d1 :: Digit d2 :: Digit d3 :: Digit d4 :: t ->
                d1 * 1000 + d2 * 100 + d3 * 10 + d4, c::t
            | c :: OneToNine d1 :: Digit d2 :: Digit d3 :: t ->
                d1 * 100 + d2 * 10 + d3, c::t
            | c :: OneToNine d1 :: Digit d2 :: t ->
                d1 * 10 + d2, c::t
            | c :: OneToNine d :: t ->
                d, c::t
            // 2dw -> 2, dw
            | OneToNine d1 :: Digit d2 :: Digit d3 :: Digit d4 :: t ->
                d1 * 1000 + d2 * 100 + d3 * 10 + d4, t
            | OneToNine d1 :: Digit d2 :: Digit d3 :: t ->
                d1 * 100 + d2 * 10 + d3, t
            | OneToNine d1 :: Digit d2 :: t ->
                d1 * 10 + d2, t
            | OneToNine d :: t -> d, t
            | _ -> 1, keyList

        let run = getCommand multiplier
        LoggingService.LogDebug (sprintf "%A %A" state.mode keyList)
        let newState =
            match keyList with
            | [ FindChar m; c ] -> { state with findCharCommand = run Move ( m c ) |> Some }
            | _ -> state

        let action =
            match state.mode, keyList with
            | VisualBlockMode, [ Escape ] -> [ run Move SelectionStart; run (SwitchMode NormalMode) Nothing ]
            | _, [ Escape ] -> [ run (SwitchMode NormalMode) Nothing; run Move Left ]
            | NotInsertMode, [ Movement m ] -> [ run Move m ]
            | NormalMode, [ "c"; FindChar m; c ] -> [ run Delete (m c); run (SwitchMode InsertMode) Nothing ]
            | NotInsertMode, [ FindChar m; c ] -> [ run Move (m c) ]
            | NormalMode, [ "c"; Movement m ] -> [ run Delete m; run (SwitchMode InsertMode) Nothing ]
            | NormalMode, [ Action action; Movement m ] -> [ run action m ]
            | NormalMode, [ "u" ] -> [ run Undo Nothing ]
            | NormalMode, [ "d"; "d" ] -> [ run Delete WholeLineIncludingDelimiter ]
            | NormalMode, [ "c"; "c" ] -> [ run Delete WholeLine; run (SwitchMode InsertMode) Nothing ]
            | NormalMode, [ "y"; "y" ] -> [ run Yank WholeLineIncludingDelimiter ]
            | NormalMode, [ "C" ] -> [ run Delete EndOfLine; run (SwitchMode InsertMode) Nothing ]
            | NormalMode, [ "D" ] -> [ run Delete EndOfLine ]
            | NormalMode, [ "x" ] -> [ run Delete CurrentLocation; run Move EnsureCursorBeforeDelimiter ]
            | NormalMode, [ "p" ] -> [ run (Put After) Nothing ]
            | NormalMode, [ "P" ] -> [ run (Put Before) Nothing ]
            | NormalMode, [ Action action; FindChar m; c ] -> [ run action (m c) ]
            | NormalMode, [ Action action; "i"; BlockDelimiter c ] -> [ run action (InnerBlock c) ]
            | NormalMode, [ Action action; "a"; BlockDelimiter c ] -> [ run action (ABlock c) ]
            | NormalMode, [ ModeChange mode ] -> [ run (SwitchMode mode) Nothing ]
            | NormalMode, [ "a" ] -> [ run Move RightIncludingDelimiter; run (SwitchMode InsertMode) Nothing ]
            | NormalMode, [ "A" ] -> [ run Move EndOfLine; run (SwitchMode InsertMode) Nothing ]
            | NormalMode, [ "O" ] -> [ run (InsertLine After) Nothing; run (SwitchMode InsertMode) Nothing ]
            | NormalMode, [ "o" ] -> [ run (InsertLine Before) Nothing; run (SwitchMode InsertMode) Nothing ]
            | NormalMode, [ Action _ ] -> wait
            | NormalMode, [ Action _; "i" ] -> wait
            | NormalMode, [ Action _; "a" ] -> wait
            | NotInsertMode, [ FindChar _; ] -> wait
            | NotInsertMode, [ Action _; FindChar _; ] -> wait
            | NormalMode, [ "g"; "g" ] -> [ run Move StartOfDocument ]
            | NormalMode, [ "g" ] -> wait
            | NormalMode, [ "." ] -> [ run RepeatLastAction Nothing ]
            | NormalMode, [ ";" ] -> match state.findCharCommand with Some command -> [ command ] | None -> []
            | VisualModes, [ Movement m ] -> [ run Move m ]
            | VisualBlockMode, [ "I" ] -> [ run BlockInsert Nothing; ]
            | VisualModes, [ "x" ] -> [ run Delete Selection; run (SwitchMode NormalMode) Nothing ]
            | VisualModes, [ "d" ] -> [ run Delete Selection; run (SwitchMode NormalMode) Nothing ]
            | VisualModes, [ "c" ] -> [ run Delete Selection; run (SwitchMode InsertMode) Nothing ]
            | VisualModes, [ "y" ] -> [ run Yank Selection; run (SwitchMode NormalMode) Nothing ]
            | _, _ :: _ :: _ :: _ :: t -> [ run ResetKeys Nothing ]
            | _, [] when multiplier > 1 -> wait
            | _ -> [ run ResetKeys Nothing ]
        multiplier, action, newState

    let handleKeyPress state (keyPress:KeyDescriptor) editorData =
        let newKeys =
            match state.mode, keyPress.KeyChar with
            | _, c when keyPress.ModifierKeys = ModifierKeys.Control ->
                state.keys @ [sprintf "<C-%c>" c]
            | NotInsertMode, c when keyPress.KeyChar <> '\000' ->
                state.keys @ [c |> string]
            | VisualModes, c | InsertMode, c ->
                match keyPress.SpecialKey with
                | SpecialKey.Escape -> state.keys @ ["<esc>"]
                | SpecialKey.Left -> state.keys @ ["h"]
                | SpecialKey.Down -> state.keys @ ["j"]
                | SpecialKey.Up -> state.keys @ ["k"]
                | SpecialKey.Right -> state.keys @ ["l"]
                | _ -> state.keys
            | _ -> state.keys
        let newState = { state with keys = newKeys }
        let multiplier, action, newState = parseKeys newState
        LoggingService.LogDebug (sprintf "%A %A" multiplier action)
        let rec performActions actions' state handled =
            match actions' with
            | [] -> state, handled
            | h::t ->
                if h.commandType = DoNothing then
                    newState, true
                else
                    let newState = runCommand state editorData h
                    performActions t { newState with keys = [] } true

        match action with
        | [ a ] when a.commandType = RepeatLastAction -> // "."
            performActions state.lastAction newState false
        | actions ->
            performActions actions { newState with lastAction = actions } false

    let mutable vimState = { keys=[]; mode=NormalMode; visualStartOffset=0; findCharCommand=None; lastAction=[] }

    override x.Initialize() =
        let editorData = x.Editor.GetContent<ITextEditorDataProvider>().GetTextEditorData()
        editorData.Caret.Mode <- CaretMode.Block

    override x.KeyPress descriptor =
        let editorData = x.Editor.GetContent<ITextEditorDataProvider>().GetTextEditorData()
        let oldState = vimState

        let newState, handledKeyPress = handleKeyPress vimState descriptor editorData
        LoggingService.LogDebug (sprintf "%A %A" newState handledKeyPress)
        vimState <- newState
        match oldState.mode with
        | InsertMode -> base.KeyPress descriptor
        | VisualMode -> false
        | _ -> not handledKeyPress
