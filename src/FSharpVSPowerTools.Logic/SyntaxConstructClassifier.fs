﻿namespace FSharpVSPowerTools.SyntaxColoring

open System
open Microsoft.VisualStudio.Text
open Microsoft.VisualStudio.Text.Editor
open Microsoft.VisualStudio.Text.Classification
open Microsoft.VisualStudio.Shell.Interop
open FSharpVSPowerTools
open FSharpVSPowerTools.Core
open FSharpVSPowerTools.Core.SourceCodeClassifier
open FSharpVSPowerTools.ProjectSystem
open FSharp.CompilerBinding

[<NoComparison>]
type private ClassifierState =
    { SnapshotSpan: SnapshotSpan
      Spans: CategorizedColumnSpan[] }

type SyntaxConstructClassifier (doc: ITextDocument, classificationRegistry: IClassificationTypeRegistryService,
                                vsLanguageService: VSLanguageService, serviceProvider: IServiceProvider) as self = 
    
    let getClassficationType cat =
        match cat with
        | ReferenceType -> Some "FSharp.ReferenceType"
        | ValueType -> Some "FSharp.ValueType"
        | PatternCase -> Some "FSharp.PatternCase"
        | Function -> Some "FSharp.Function"
        | MutableVar -> Some "FSharp.MutableVar"
        | Quotation -> Some "FSharp.Quotation"
        | Module -> Some "FSharp.Module"
        | _ -> None
        |> Option.map classificationRegistry.GetClassificationType

    let classificationChanged = Event<_,_>()
    let state = Atom None
    let mutable isWorking = false

    let getProject() = 
        maybe {
            let dte = serviceProvider.GetService<EnvDTE.DTE, SDTE>()
            let! projectItem = Option.attempt (fun _ -> dte.Solution.FindProjectItem doc.FilePath)
            return! ProjectProvider.createForFileInProject doc.FilePath projectItem.ContainingProject }
    
    let updateSyntaxConstructClassifiers() =
        let snapshot = doc.TextBuffer.CurrentSnapshot
        let currentState = state.Value
        if not isWorking then
            let needUpdate =
                match currentState with
                | None -> true
                | Some state -> state.SnapshotSpan.Snapshot <> snapshot

            if needUpdate then
                isWorking <- true
                match getProject() with
                | Some project ->
                    debug "[SyntaxConstructClassifier] - Effective update"
                    async {
                        try
                            try
                                let! allSymbolsUses, lexer =
                                    vsLanguageService.GetAllUsesOfAllSymbolsInFile (
                                        snapshot, doc.FilePath, project, AllowStaleResults.MatchingSource)
                                let! parseResults = vsLanguageService.ParseFileInProject(snapshot, doc.FilePath, project)

                                let spans = 
                                    getCategoriesAndLocations (allSymbolsUses, parseResults.ParseTree, lexer)
                                    |> Array.sortBy (fun { WordSpan = { Line = line }} -> line)
                        
                                state.Swap (fun _ -> 
                                    Some { SnapshotSpan = SnapshotSpan (snapshot, 0, snapshot.Length)
                                           Spans = spans }) |> ignore
                                // TextBuffer is null if a solution is closed at this moment
                                if doc.TextBuffer <> null then
                                    let currentSnapshot = doc.TextBuffer.CurrentSnapshot
                                    let snapshot = SnapshotSpan(currentSnapshot, 0, currentSnapshot.Length)
                                    classificationChanged.Trigger(self, ClassificationChangedEventArgs(snapshot))
                            with e -> Logging.logException e
                        finally isWorking <- false
                    } |> Async.Start
                | None -> ()
    
    let _ = DocumentEventsListener ([ViewChange.bufferChangedEvent doc.TextBuffer], 200us, 
                                    fun() -> try updateSyntaxConstructClassifiers() 
                                             with e -> Logging.logException e)

    let getClassificationSpans (snapshotSpan: SnapshotSpan) =
        match state.Value with
        | Some state ->
            // we get additional 10 lines above the current snapshot in case the user inserts some line
            // while we were getting locations from FCS. It's not as reliable though. 
            let spanStartLine = max 0 (snapshotSpan.Start.GetContainingLine().LineNumber + 1 - 10)
            let spanEndLine = (snapshotSpan.End - 1).GetContainingLine().LineNumber + 1
            let spans =
                state.Spans
                // locations are sorted, so we can safely filter them efficently
                |> Seq.skipWhile (fun { WordSpan = { Line = line }} -> line < spanStartLine)
                |> Seq.choose (fun loc -> 
                    maybe {
                        let! clType = getClassficationType loc.Category
                        let! span = fromPos state.SnapshotSpan.Snapshot (loc.WordSpan.ToRange())
                        return clType, span.TranslateTo(snapshotSpan.Snapshot, SpanTrackingMode.EdgeExclusive) 
                    })
                |> Seq.takeWhile (fun (_, span) -> span.Start.GetContainingLine().LineNumber <= spanEndLine)
                |> Seq.map (fun (clType, span) -> ClassificationSpan(span, clType))
                |> Seq.toArray
            spans
        | None -> [||]


    interface IClassifier with
        // it's called for each visible line of code
        member x.GetClassificationSpans(snapshotSpan: SnapshotSpan) = 
            try getClassificationSpans snapshotSpan :> _
            with e -> Logging.logException e; upcast [||]
        
        [<CLIEvent>]
        member x.ClassificationChanged = classificationChanged.Publish