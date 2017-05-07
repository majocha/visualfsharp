namespace Microsoft.VisualStudio.FSharp.Editor

open System.ComponentModel.Composition
open System
open System.Windows
open System.Windows.Controls
open System.Windows.Documents

open Microsoft.CodeAnalysis
open Microsoft.CodeAnalysis.Classification
open Microsoft.CodeAnalysis.Editor
open Microsoft.CodeAnalysis.Editor.Shared.Utilities
open Microsoft.CodeAnalysis.Editor.Shared.Extensions
open Microsoft.CodeAnalysis.Editor.Implementation.IntelliSense.QuickInfo

open Microsoft.VisualStudio.Language.Intellisense
open Microsoft.VisualStudio.Utilities
open Microsoft.VisualStudio.PlatformUI

open Microsoft.FSharp.Compiler

open Internal.Utilities.StructuredFormat

module private SessionHandling =
    let mutable currentSession = None
    
    [<Export (typeof<IQuickInfoSourceProvider>)>]
    [<Name (FSharpProviderConstants.SessionCapturingProvider)>]
    [<Order (After = PredefinedQuickInfoProviderNames.Semantic)>]
    [<ContentType (FSharpConstants.FSharpContentTypeName)>]
    type SourceProviderForCapturingSession () =
        interface IQuickInfoSourceProvider with 
            member x.TryCreateQuickInfoSource _ =
              { new IQuickInfoSource with
                  member __.AugmentQuickInfoSession(session,_,_) = currentSession <- Some session
                  member __.Dispose() = () }


type QuickInfoDocumentView(doc) =
    inherit FlowDocumentScrollViewer(Document = doc, Width = 500.0, VerticalScrollBarVisibility = ScrollBarVisibility.Hidden)
    interface IInteractiveQuickInfoContent with
        member this.IsMouseOverAggregated: bool = 
            this.IsMouseOver
        member this.KeepQuickInfoOpen: bool =
            if isNull this.Selection then false
            else not this.Selection.IsEmpty

[<Export>]
type internal QuickInfoViewProvider
    [<ImportingConstructor>]
    (
        // lazy to try to mitigate #2756 (wrong tooltip font)
        typeMap: Lazy<ClassificationTypeMap>,
        glyphService: IGlyphService
    ) =

    let styles = ResourceDictionary(Source = Uri(@"/FSharp.UIResources;component/HyperlinkStyles.xaml", UriKind.Relative))

    let getStyle() : Style =
        let key =
            if Settings.QuickInfo.DisplayLinks then
                match Settings.QuickInfo.UnderlineStyle with
                | QuickInfoUnderlineStyle.Solid -> "solid_underline"
                | QuickInfoUnderlineStyle.Dot -> "dot_underline"
                | QuickInfoUnderlineStyle.Dash -> "dash_underline"
            else "no_underline"
        downcast styles.[key]

    let formatMap = lazy typeMap.Value.ClassificationFormatMapService.GetClassificationFormatMap "tooltip"

    let layoutTagToFormatting (layoutTag: LayoutTag) =
        layoutTag
        |> RoslynHelpers.roslynTag
        |> ClassificationTags.GetClassificationTypeName
        |> typeMap.Value.GetClassificationType
        |> formatMap.Value.GetTextProperties
    
    let formatText (navigation: QuickInfoNavigation) (content: Layout.TaggedText seq) =

        let navigateAndDismiss range _ =
            navigation.NavigateTo range
            SessionHandling.currentSession |> Option.iter ( fun session -> session.Dismiss() )

        let secondaryToolTip range =
            let t = ToolTip(Content = navigation.RelativePath range)
            DependencyObjectExtensions.SetDefaultTextProperties(t, formatMap.Value)
            let color = VSColorTheme.GetThemedColor(EnvironmentColors.ToolTipBrushKey)
            t.Background <- Media.SolidColorBrush(Media.Color.FromRgb(color.R, color.G, color.B))
            t

        let p = Paragraph()

        p.Inlines.AddRange(
            seq { 
                for taggedText in content do
                    let run = Run taggedText.Text
                    let inl =
                        match taggedText with
                        | :? Layout.NavigableTaggedText as nav when navigation.IsTargetValid nav.Range ->                        
                            let h = Hyperlink(run, ToolTip = secondaryToolTip nav.Range)
                            h.Click.Add <| navigateAndDismiss nav.Range
                            h :> Inline
                        | _ -> run :> _
                    DependencyObjectExtensions.SetTextProperties (inl, layoutTagToFormatting taggedText.Tag)
                    yield inl
            })
        p

        //let createTextLinks () =
        //    let tb = TextBlock(TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.None)
        //    DependencyObjectExtensions.SetDefaultTextProperties(tb, formatMap.Value)
        //    tb.Inlines.AddRange inlines
        //    if tb.Inlines.Count = 0 then tb.Visibility <- Visibility.Collapsed
        //    tb.Resources.
        //    tb :> FrameworkElement
            
        //{ new IDeferredQuickInfoContent with member x.Create() = createTextLinks() }

    let content doc =
        { new IDeferredQuickInfoContent with
            member __.Create() =
                let viewer = QuickInfoDocumentView(doc())
                DependencyObjectExtensions.SetDefaultTextProperties(viewer, formatMap.Value)
                viewer.Resources.[typeof<Hyperlink>] <- getStyle()
                upcast viewer }

    member __.ProvideContent(glyph: Glyph, description: Layout.TaggedText seq, documentation, typeParameterMap, usage, exceptions, navigation: QuickInfoNavigation) =
        let _glyphContent = SymbolGlyphDeferredContent(glyph, glyphService)
        let document() =
            let doc = FlowDocument()
            [ description
              documentation
              typeParameterMap
              usage
              exceptions ]
            |> Seq.filter (Seq.isEmpty >> not)
            |> Seq.iter (formatText navigation >> doc.Blocks.Add)
            doc
        content document
