// Copyright (c) Microsoft Corporation.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

#nowarn "40"

namespace rec Microsoft.VisualStudio.FSharp.ProjectSystem 

    open Helpers 
    open System
    open System.Threading
    open System.Reflection 
    open System.CodeDom
    open System.CodeDom.Compiler
    open System.Runtime.CompilerServices
    open System.Runtime.InteropServices
    open System.Runtime.Serialization
    open System.Collections.Generic
    open System.Collections
    open System.ComponentModel
    open System.ComponentModel.Design
    open System.Text.RegularExpressions
    open System.Diagnostics
    open System.IO
    open System.Drawing
    open System.Globalization
    open System.Text
    open System.Windows.Controls

    open Microsoft.Win32

    open Microsoft.VisualStudio
    open Microsoft.VisualStudio.Shell
    open Microsoft.VisualStudio.Shell.Interop
    open Microsoft.VisualStudio.Shell.Flavor
    open Microsoft.VisualStudio.OLE.Interop
    open Microsoft.VisualStudio.FSharp.ProjectSystem
    open Microsoft.VisualStudio.FSharp.LanguageService
    open Microsoft.VisualStudio.FSharp.ProjectSystem.Automation
    open Microsoft.VisualStudio.FSharp.Editor
    open Microsoft.VisualStudio.Editors
    open Microsoft.VisualStudio.Editors.PropertyPages
    
    open EnvDTE

    module internal VSHiveUtilities =
            /// For a given sub-hive, check to see if a 3rd party has specified any
            /// custom/extended property pages.
            /// Generally, we'll find the property page GUIDs in the following locations:
            /// HKLM\SOFTWARE\Microsoft\VisualStudio\<VS_VERSION>\<guidFSharpProjectFactoryStringWithCurlies>\ConfigPropertyPages
            /// HKLM\SOFTWARE\Microsoft\VisualStudio\<VS_VERSION>\<guidFSharpProjectFactoryStringWithCurlies>\CommonPropertyPages
            /// HKLM\SOFTWARE\Microsoft\VisualStudio\<VS_VERSION>\<guidFSharpProjectFactoryStringWithCurlies>\PriorityPropertyPages
            let GetExtendedPropertyPages  (pphive : RegistryKey) =
                if null = pphive then [||]
                else
                // read the sub keys for the property key hive
                let subKeys = pphive.GetSubKeyNames()
                
                // filter out keys that aren't proper GUIDs
                let isProperGuidSubKey (g : string) =
                    try
                        // we'll accept any kind of guid
                        System.Guid(g)
                    with _ -> Unchecked.defaultof<System.Guid>
                
                subKeys |> Array.map isProperGuidSubKey |> Array.filter (fun g -> g <> Unchecked.defaultof<System.Guid>)

            let lazyPropertyPages =
                lazy (
                        let vsHive = Microsoft.VisualStudio.Shell.VSRegistry.RegistryRoot(Microsoft.VisualStudio.Shell.Interop.__VsLocalRegistryType.RegType_Configuration)
                        let fsHive = vsHive.OpenSubKey("Projects").OpenSubKey(GuidList.guidFSharpProjectFactoryStringWithCurlies)
                        let commonPropertyPages = GetExtendedPropertyPages(fsHive.OpenSubKey("CommonPropertyPages"))
                        let configPropertyPages = GetExtendedPropertyPages(fsHive.OpenSubKey("ConfigPropertyPages"))
                        let priorityPropertyPages = GetExtendedPropertyPages(fsHive.OpenSubKey("PriorityPropertyPages")) 
                        commonPropertyPages, configPropertyPages, priorityPropertyPages
                )
                    
            let getCommonExtendedPropertyPages() = match lazyPropertyPages.Force() with (common,_,_) -> common
            let getConfigExtendedPropertyPages() = match lazyPropertyPages.Force() with (_,config,_) -> config
            let getPriorityExtendedPropertyPages() = match lazyPropertyPages.Force() with (_,_,priority) -> priority

//////////////////////

    // An IProjectSite object with hot-swappable inner implementation
    type internal DynamicProjectSite(origInnerImpl : Microsoft.VisualStudio.FSharp.LanguageService.IProjectSite) =
        let mutable inner = origInnerImpl
        member x.SetImplementation newInner =
            inner <- newInner
        // This interface is thread-safe, assuming "inner" is thread-safe
        interface Microsoft.VisualStudio.FSharp.LanguageService.IProjectSite with
            member ips.SourceFilesOnDisk() = inner.SourceFilesOnDisk()
            member ips.DescriptionOfProject() = inner.DescriptionOfProject()
            member ips.CompilerFlags() = inner.CompilerFlags()
            member ips.ProjectFileName() = inner.ProjectFileName()
            member ips.AdviseProjectSiteChanges(callbackOwnerKey,callback) = inner.AdviseProjectSiteChanges(callbackOwnerKey, callback)
            member ips.AdviseProjectSiteCleaned(callbackOwnerKey,callback) = inner.AdviseProjectSiteCleaned(callbackOwnerKey, callback)
            member ips.AdviseProjectSiteClosed(callbackOwnerKey,callback) = inner.AdviseProjectSiteClosed(callbackOwnerKey, callback)
            member ips.ErrorListTaskProvider() = inner.ErrorListTaskProvider()
            member ips.ErrorListTaskReporter() = inner.ErrorListTaskReporter()
            member ips.TargetFrameworkMoniker = inner.TargetFrameworkMoniker
            member ips.ProjectGuid = inner.ProjectGuid
            member ips.IsIncompleteTypeCheckEnvironment = false
            member ips.LoadTime = inner.LoadTime 
            member ips.ProjectProvider = inner.ProjectProvider

    type internal ProjectSiteOptionLifetimeState =
        | Opening=1  // The project has been opened, but has not yet called Compile() to compute sources/flags
        | Opened=2   // The project is open, has some (possibly stale) sources/flags info
        | Closed=3   // The project closed, has stale sources/flags info

    type internal ProjectSiteOptionLifetime() =
        let mutable state = ProjectSiteOptionLifetimeState.Opening
        let mutable projectSite : DynamicProjectSite option = None
        // This member is thread-safe
        member x.State = state
        // This member is thread-safe
        member x.GetProjectSite() =
            Debug.Assert(state <> ProjectSiteOptionLifetimeState.Opening, "ProjectSite is not available")
            projectSite.Value :> Microsoft.VisualStudio.FSharp.LanguageService.IProjectSite
        member x.Open(site) =
            Debug.Assert((state = ProjectSiteOptionLifetimeState.Opening), "Called Open, but not in Opening state")
            state <- ProjectSiteOptionLifetimeState.Opened
            projectSite <- Some(new DynamicProjectSite(site))
        member x.Close(site) =
            Debug.Assert((state = ProjectSiteOptionLifetimeState.Opened || state = ProjectSiteOptionLifetimeState.Opening), "Called Close, but not in Opened or Opening state")
            state <- ProjectSiteOptionLifetimeState.Closed
            match projectSite with
            |   Some ps -> ps.SetImplementation(site)
            |   None -> projectSite <- Some (new DynamicProjectSite(site))

    type internal MyVSConstants =
        static member ExploreFolderInWindows = 1635u

    type internal Notifier() =
        let notificationsDict = new System.Collections.Generic.Dictionary<string,Microsoft.VisualStudio.FSharp.LanguageService.AdviseProjectSiteChanges>()
        member this.Notify() =
            for kvp in notificationsDict do
                kvp.Value.Invoke()
        member this.Advise(callbackOwnerKey, callback) =
            notificationsDict.[callbackOwnerKey] <- callback

    // Used to get us sorted appropriately with the other MSFT products in the splash screen and about box
    [<Guid("591E80E4-5F44-11d3-8BDC-00C04F8EC28C")>]
    [<InterfaceType(ComInterfaceType.InterfaceIsIUnknown)>]
    [<ComImport>] 
    [<Interface>] 
    [<ComVisible(true)>]
    [<System.Runtime.InteropServices.ClassInterface(ClassInterfaceType.None)>]
    type public IVsMicrosoftInstalledProduct =
        inherit IVsInstalledProduct
        abstract IdBmpSplashM : byref<uint32> -> unit
        abstract OfficialNameM : on : byref<string> -> unit
        abstract ProductIDM : pid : byref<string> -> unit
        abstract ProductDetailsM : pd : byref<string> -> unit
        abstract IdIcoLogoForAboutboxM : byref<uint32> -> unit            
        abstract ProductRegistryName : prn : byref<string> -> unit

    exception internal ExitedOk
    exception internal ExitedWithError

    //--------------------------------------------------------------------------------------
    // The big mutually recursive set of types.
    //    FSharpProjectPackage
    //    EditorFactory
    //    FSharpProjectFactory
    //    ....

    [<ProvideOptionPage(typeof<Microsoft.VisualStudio.FSharp.Interactive.FsiPropertyPage>,
                        "F# Tools", "F# Interactive",   // category/sub-category on Tools>Options...
                        6000s,      6001s,              // resource id for localisation of the above
                        true)>]                         // true = supports automation
    [<ProvideKeyBindingTable("{dee22b65-9761-4a26-8fb2-759b971d6dfc}", 6001s)>] // <-- resource ID for localised name
    [<ProvideToolWindow(typeof<Microsoft.VisualStudio.FSharp.Interactive.FsiToolWindow>, 
                        // The following should place the ToolWindow with the OutputWindow by default.
                        Orientation=ToolWindowOrientation.Bottom,
                        Style=VsDockStyle.Tabbed,
                        PositionX = 0,
                        PositionY = 0,
                        Width = 360,
                        Height = 120,
                        Window="34E76E81-EE4A-11D0-AE2E-00A0C90FFFC3")>] // where 34E76E81-EE4A-11D0-AE2E-00A0C90FFFC3 = outputToolWindow
    [<Guid(GuidList.guidFSharpProjectPkgString)>]
    type internal FSharpProjectPackage() as this = 
            inherit ProjectPackage() 

            let mutable vfsiToolWindow = Unchecked.defaultof<Microsoft.VisualStudio.FSharp.Interactive.FsiToolWindow>
            let GetToolWindowAsITestVFSI() =
                if vfsiToolWindow = Unchecked.defaultof<_> then
                    vfsiToolWindow <- this.FindToolWindow(typeof<Microsoft.VisualStudio.FSharp.Interactive.FsiToolWindow>, 0, true) :?> Microsoft.VisualStudio.FSharp.Interactive.FsiToolWindow
                vfsiToolWindow :> Microsoft.VisualStudio.FSharp.Interactive.ITestVFSI

            // FSI-LINKAGE-POINT: unsited init
            do Microsoft.VisualStudio.FSharp.Interactive.Hooks.fsiConsoleWindowPackageCtorUnsited (this :> Package)

            // Get the ComponentManager one time at the start
            let mgr : IOleComponentManager = this.GetService(typeof<SOleComponentManager>) :?> IOleComponentManager

            let mutable componentID = 0u

            let thisLock = obj()

            member this.RegisterForIdleTime() =

                if not (isNull mgr) && componentID = 0u then
                    lock (thisLock) (fun _ -> 
                        if componentID = 0u then
                            let crinfo = Array.zeroCreate<OLECRINFO>(1)
                            let mutable crinfo0 = crinfo.[0]
                            crinfo0.cbSize <- Marshal.SizeOf(typeof<OLECRINFO>) |> uint32
                            crinfo0.grfcrf <- uint32 (_OLECRF.olecrfNeedIdleTime ||| _OLECRF.olecrfNeedPeriodicIdleTime)
                            crinfo0.grfcadvf <- uint32 (_OLECADVF.olecadvfModal ||| _OLECADVF.olecadvfRedrawOff ||| _OLECADVF.olecadvfWarningsOff)
                            crinfo0.uIdleTimeInterval <- 1000u
                            crinfo.[0] <- crinfo0
     
                            mgr.FRegisterComponent(this, crinfo, &componentID) |> ignore
                    )

            /// This method loads a localized string based on the specified resource.

            /// <param name="resourceName">Resource to load</param>
            /// <returns>String loaded for the specified resource</returns>
            member this.GetResourceString(resourceName:string) =
                let mutable resourceValue = null
                let resourceManager = GetService2<SVsResourceManager,IVsResourceManager>(this :> System.IServiceProvider)

                let mutable packageGuid = this.GetType().GUID
                let hr = resourceManager.LoadResourceString(&packageGuid, -1, resourceName, &resourceValue)
                Microsoft.VisualStudio.ErrorHandler.ThrowOnFailure(hr) |> ignore
                resourceValue

            override this.Initialize() =
                UIThread.CaptureSynchronizationContext()

                base.Initialize()

                let fSharpEditorFactory = new Microsoft.VisualStudio.FSharp.ProjectSystem.FSharpEditorFactory(this);
                this.RegisterEditorFactory(fSharpEditorFactory);

                // read list of available FSharp.Core versions
                do
                    let nullService = 
                        { new Microsoft.VisualStudio.FSharp.ProjectSystem.IFSharpCoreVersionLookupService with 
                            member this.ListAvailableFSharpCoreVersions(_) = Array.empty }

                    let service = 
                        match Internal.Utilities.FSharpEnvironment.BinFolderOfDefaultFSharpCompiler with
                        | None -> nullService
                        | Some path ->
                            try
                                let supportedRuntimesXml = System.Xml.Linq.XDocument.Load(Path.Combine(path, "SupportedRuntimes.xml"))
                                let tryGetAttr (el : System.Xml.Linq.XElement) attr = 
                                    match el.Attribute(System.Xml.Linq.XName.Get attr) with
                                    | null -> None
                                    | x -> Some x.Value
                                let flatList = 
                                    supportedRuntimesXml.Root.Elements(System.Xml.Linq.XName.Get "TargetFramework")
                                    |> Seq.choose (fun tf ->
                                        match tryGetAttr tf "Identifier", tryGetAttr tf "Version", tryGetAttr tf "Profile" with
                                        | Some key1, Some key2, _ 
                                        | Some key1, _, Some key2 ->
                                            Some(
                                                key1, // identifier
                                                key2, // version or profile
                                                [|
                                                    for asm in tf.Elements(System.Xml.Linq.XName.Get "Assembly") do
                                                        let version = asm.Attribute(System.Xml.Linq.XName.Get "Version")
                                                        let description = asm.Attribute(System.Xml.Linq.XName.Get "Description")
                                                        match version, description with
                                                        | null, _ | _, null -> ()
                                                        | version, description ->
                                                            yield Microsoft.VisualStudio.FSharp.ProjectSystem.FSharpCoreVersion(version.Value, description.Value)
                                                |]
                                            )
                                        | _ -> None
                                     )
                                     
                                    |> Seq.toList
                                let (_, _, v2) = flatList |> List.find(fun (k1, k2, _) -> k1 = FSharpSDKHelper.NETFramework && k2 = FSharpSDKHelper.v20)
                                let (_, _, v4) = flatList |> List.find(fun (k1, k2, _) -> k1 = FSharpSDKHelper.NETFramework && k2 = FSharpSDKHelper.v40)
                                let (_, _, v45) = flatList |> List.find(fun (k1, k2, _) -> k1 = FSharpSDKHelper.NETFramework && k2 = FSharpSDKHelper.v45)
                                {
                                    new Microsoft.VisualStudio.FSharp.ProjectSystem.IFSharpCoreVersionLookupService with
                                        member this.ListAvailableFSharpCoreVersions(targetFramework) =
                                            if targetFramework.Identifier = FSharpSDKHelper.NETFramework
                                            then 
                                                // for .NETFramework we distinguish between 2.0, 4.0 and 4.5
                                                if targetFramework.Version.Major < 4 then v2 
                                                elif targetFramework.Version.Major = 4 && targetFramework.Version.Minor < 5 then v4 
                                                else v45
                                            else 
                                                // for other target frameworks we assume that they are distinguished by the profile
                                                let result = 
                                                    flatList
                                                    |> List.tryPick(fun (k1, k2, list) -> 
                                                        if k1 = targetFramework.Identifier && k2 = targetFramework.Profile then Some list else None
                                                    )
                                                match result with
                                                | Some list -> list
                                                | None ->
                                                    Debug.Assert(false, sprintf "Unexpected target framework identifier '%O'" targetFramework)
                                                    [||]
                                }
                            with _ -> nullService
                    (this :> IServiceContainer).AddService(typeof<Microsoft.VisualStudio.FSharp.ProjectSystem.IFSharpCoreVersionLookupService>, service, promote = true)


                this.RegisterProjectFactory(new FSharpProjectFactory(this))
                //this.RegisterProjectFactory(new FSharpWPFProjectFactory(this :> IServiceProvider))

                // was used to ensure the LS has been initialized, because the TypeProviderSecurityGlobals 
                // global state was needed for e.g. Tools\Options
                //TODO the TypeProviderSecurityGlobals does not exists anymore, remove the initialization?
                this.GetService(typeof<FSharpLanguageService>) |> ignore  

                // FSI-LINKAGE-POINT: sited init
                let commandService = this.GetService(typeof<IMenuCommandService>) :?> OleMenuCommandService // FSI-LINKAGE-POINT
                Microsoft.VisualStudio.FSharp.Interactive.Hooks.fsiConsoleWindowPackageInitalizeSited (this :> Package) commandService
                // FSI-LINKAGE-POINT: private method GetDialogPage forces fsi options to be loaded
                let _fsiPropertyPage = this.GetDialogPage(typeof<Microsoft.VisualStudio.FSharp.Interactive.FsiPropertyPage>)

                this.RegisterForIdleTime()
                ()

            /// This method is called during Devenv /Setup to get the bitmap to
            /// display on the splash screen for this package.
            /// This method may be deprecated - IdIcoLogoForAboutbox should now be called instead
            //  REVIEW: If this turns out to be true, remove the IdBmpSplash resource
            member this.getIdBmpSplash(pIdBmp:byref<uint32>) =
                pIdBmp <- 0u ;
                VSConstants.S_OK
            
            /// This method is called to get the icon that will be displayed in the
            /// Help About dialog when this package is selected.

            /// This methods provides the product official name, it will be
            /// displayed in the help about dialog.
        
            /// <param name="pbstrName">Out parameter to which to assign the product name</param>
            /// <returns>HRESULT, indicating success or failure</returns>
            member this.getOfficialName(pbstrName:byref<string>) =
                pbstrName <- this.GetResourceString("@ProductName10") ;
                VSConstants.S_OK
                
            /// This methods provides the product version, it will be
            /// displayed in the help about dialog.
        
            /// <param name="pbstrPID">Out parameter to which to assign the version number</param>
            /// <returns>HRESULT, indicating success or failure</returns>
            member this.getProductID(pbstrPID:byref<string>) =
                pbstrPID <- this.GetResourceString("@ProductID") ;
                VSConstants.S_OK

            /// This methods provides the product description, it will be
            /// displayed in the help about dialog.
        
            /// <param name="pbstrProductDetails">Out parameter to which to assign the description of the package</param>
            /// <returns>HRESULT, indicating success or failure</returns>
            member this.getProductDetails(pbstrProductDetails:byref<string>) =
                pbstrProductDetails <- this.GetResourceString("@ProductDetails10")
                VSConstants.S_OK

            /// <param name="pIdIco">The resource id corresponding to the icon to display on the Help About dialog</param>
            /// <returns>HRESULT, indicating success or failure</returns>
            member this.getIdIcoLogoForAboutbox(pIdIco:byref<uint32>) =
                pIdIco <- 400u
                VSConstants.S_OK

            override this.Dispose(disposing) =
                try
                    if not (isNull mgr) && componentID <> 0u then
                        lock (thisLock) (fun _ ->
                            if componentID <> 0u then
                                mgr.FRevokeComponent(componentID) |> ignore
                                componentID <- 0u)
                finally
                    base.Dispose(disposing)

            interface Microsoft.VisualStudio.FSharp.Interactive.ITestVFSI with
                member this.SendTextInteraction(s:string) =
                    GetToolWindowAsITestVFSI().SendTextInteraction(s)
                member this.GetMostRecentLines(n:int) : string[] =
                    GetToolWindowAsITestVFSI().GetMostRecentLines(n)
            
            interface IOleComponent with
                override this.FContinueMessageLoop(_uReason:uint32, _pvLoopData:IntPtr, _pMsgPeeked:MSG[]) = 
                    1

                override this.FDoIdle(grfidlef:uint32) =
                    // see e.g "C:\Program Files\Microsoft Visual Studio 2008 SDK\VisualStudioIntegration\Common\IDL\olecm.idl" for details
                    //Trace.Print("CurrentDirectoryDebug", (fun () -> sprintf "curdir='%s'\n" (System.IO.Directory.GetCurrentDirectory())))  // can be useful for watching how GetCurrentDirectory changes
                    let periodic = (grfidlef &&& (uint32 _OLEIDLEF.oleidlefPeriodic)) <> 0u
                    if periodic && not (isNull mgr) && mgr.FContinueIdle() <> 0 then
                        TaskReporterIdleRegistration.DoIdle(mgr)
                    else
                        0

                override this.FPreTranslateMessage(_pMsg) = 0

                override this.FQueryTerminate(_fPromptUser) = 1

                override this.FReserved1(_dwReserved, _message, _wParam, _lParam) = 1

                override this.HwndGetWindow(_dwWhich, _dwReserved) = 0n

                override this.OnActivationChange(_pic, _fSameComponent, _pcrinfo, _fHostIsActivating, _pchostinfo, _dwReserved) = ()

                override this.OnAppActivate(_fActive, _dwOtherThreadID) = ()

                override this.OnEnterState(_uStateID, _fEnter)  = ()
        
                override this.OnLoseActivation() = ()

                override this.Terminate() = ()

    /// Factory for creating our editor, creates FSharp Projects
    [<Guid(GuidList.guidFSharpProjectFactoryString)>]
    type internal FSharpProjectFactory(package:FSharpProjectPackage ) =  
            inherit ProjectFactory(package)

            override this.CreateProject() =

                // Then create the project to load.
                let project = new FSharpProjectNode(this.Package :?> FSharpProjectPackage)
                project.SetSite(GetService<IOleServiceProvider>(this.Package :> System.IServiceProvider)) |> ignore
                (project :> ProjectNode)


    /// This class is a 'fake' project factory that is used by WAP to register WAP specific information about
    /// FSharp projects.
    [<Guid("4EAD5BC6-47F1-4FCB-823D-0CD64302D5B9")>]
    type internal WAFSharpProjectFactory() = class end


    [<Guid("C15CF2F6-9005-44AD-9991-683808A8E5EA")>]
    type internal FSharpProjectNode(package:FSharpProjectPackage) as this = 
            inherit ProjectNode() 

            let mutable vsProject : VSLangProj.VSProject = null
            let mutable trackDocumentsHandle = 0u
            let mutable addFilesNotification : option<(array<string> -> unit)> = None  // this object is only used for helping re-order newly added files (VS defaults to alphabetical order)
            
            let mutable updateSolnEventsHandle = 0u
            let mutable updateSolnEventsHandle2 = 0u
            let mutable updateSolnEventsHandle3 = 0u
            let mutable updateSolnEventsHandle4 = 0u

            let mutable trackProjectRetargetingCookie = 0u
            
            let mutable actuallyBuild = true
            
            let mutable inMidstOfReloading = false
            
            // WARNING: Please avoid renaming this field or the containing type. The Visual F# Power Tools currently
            // use reflection to access this field to extract project sources and flags accurately from F# projects.  
            // See https://github.com/fsprojects/VisualFSharpPowerTools/blob/58b8e409ee6836a39b22284740706d2cf488aabe/src/FSharpVSPowerTools.Logic/ProjectProvider.fs#L42
            // for example. If necessary, this can be changed - but please just try to avoid doing a gratuitous rename.
            let mutable sourcesAndFlags : option<(array<string> * array<string>)> = None
#if DEBUG
            let logger = new Microsoft.Build.Logging.ConsoleLogger(Microsoft.Build.Framework.LoggerVerbosity.Diagnostic,
                                (fun s -> Trace.WriteLine("MSBuild: " + s)),
                                (fun _ -> ()),
                                (fun _ -> ())    )
#endif
            
            
            let projectSite = new ProjectSiteOptionLifetime()
            
            let sourcesAndFlagsNotifier = new Notifier()
            let cleanNotifier = new Notifier()
            let closeNotifier = new Notifier()
            
            [<Microsoft.FSharp.Core.DefaultValue>]
            static val mutable private imageOffset : int 
#if DEBUG
            let uiThreadId = System.Threading.Thread.CurrentThread.ManagedThreadId
            let mutable compileWasActuallyCalled = false
#endif            

            // these get initialized once and for all in SetSite()
            let mutable isInCommandLineMode = false
            let mutable accessor : IVsBuildManagerAccessor = null  
            
            //Store the number of images in ProjectNode so we know the offset of the F# icons.
            do FSharpProjectNode.imageOffset <- this.ImageHandler.ImageList.Images.Count
            do this.CanFileNodesHaveChilds <- false
            do this.OleServiceProvider.AddService(typeof<VSLangProj.VSProject>, new OleServiceProvider.ServiceCreatorCallback(this.CreateServices), false)
            do this.SupportsProjectDesigner <- true
            do this.Package <- package
            do   
                // Add in correct order, as defined by the "FSharpImageName" enum
                this.ImageHandler.AddImage(FSharpSR.GetObject("4101") :?> System.Drawing.Bitmap) // 4005 = CodeFile
                this.ImageHandler.AddImage(FSharpSR.GetObject("4100") :?> System.Drawing.Bitmap) // 4008 = EmptyProject
                this.ImageHandler.AddImage(FSharpSR.GetObject("4103") :?> System.Drawing.Bitmap) // 4006 = ScriptFile
                this.ImageHandler.AddImage(FSharpSR.GetObject("4102") :?> System.Drawing.Bitmap) // 4007 = Signature

            /// Provide mapping from our browse objects and automation objects to our CATIDs
            do 
                // The following properties classes are specific to F# so we can use their GUIDs directly
                this.AddCATIDMapping(typeof<FSharpProjectNodeProperties>, typeof<FSharpProjectNodeProperties>.GUID)
                this.AddCATIDMapping(typeof<FSharpFileNodeProperties>, typeof<FSharpFileNodeProperties>.GUID)
                // This one we use the same as F# file nodes since both refer to files
                this.AddCATIDMapping(typeof<FileNodeProperties>, typeof<FSharpFileNodeProperties>.GUID)
                this.AddCATIDMapping(typeof<ProjectConfigProperties>, typeof<ProjectConfigProperties>.GUID)

#if DEBUG
                this.SetDebugLogger(logger)
#endif
            member private this.GetCurrentFrameworkName() = 
                let tfm = this.GetTargetFrameworkMoniker()
                System.Runtime.Versioning.FrameworkName(tfm)

            member private this.CheckProjectFrameworkIdentifier(expected) =
                let currentFrameworkName = this.GetCurrentFrameworkName()
                currentFrameworkName.Identifier = expected
            
            override this.TargetFSharpCoreVersion 
                with get() : string = 
                    if this.CanUseTargetFSharpCoreReference then
                        this.GetProjectProperty(ProjectFileConstants.TargetFSharpCoreVersion)
                    else
                        let fsharpCoreRef = 
                            this.GetReferenceContainer().EnumReferences()
                            |> Seq.tryPick (
                                function
                                | :? AssemblyReferenceNode as arn when AssemblyReferenceNode.IsFSharpCoreReference arn -> Some arn
                                | _ -> None
                            )
                        match fsharpCoreRef with
                        | Some arn when arn.ResolvedAssembly <> null-> arn.ResolvedAssembly.Version.ToString()
                        | _ -> null
                    
                    
                and set(v) = 
                    if not this.CanUseTargetFSharpCoreReference then () else
                    let currentVersion = System.Version(this.TargetFSharpCoreVersion)
                    let newVersion = System.Version(v)
                    if not (currentVersion.Equals newVersion) then
                        let hasSwitchedToLatestOnlyVersionFromLegacy = 
                            let legacyVersions =
                                ["2.3.0.0"                        // .NET 2 desktop
                                 "4.3.0.0"; "4.3.1.0"; "4.4.0.0"  // .NET 4 desktop
                                 "2.3.5.0"; "2.3.5.1"; "3.47.4.0" // portable 47
                                 "3.3.1.0"; "3.7.4.0"             // portable 7
                                 "3.78.3.1"; "3.78.4.0"           // portable 78
                                 "3.259.3.1"; "3.259.4.0"]        // portable 259
                                |> List.map (fun s -> System.Version(s))
                            let latestOnlyVersions = 
                                ["4.4.1.0"                        // .NET 4 desktop
                                 "3.47.41.0"                       // portable 47
                                 "3.7.41.0"                        // portable 7
                                 "3.78.41.0"                       // portable 78
                                 "3.259.41.0"]                     // portable 259
                                |> List.map (fun s -> System.Version(s))
                            
                            (legacyVersions |> List.exists ((=) currentVersion)) && (latestOnlyVersions |> List.exists ((=) newVersion))                                

                        if hasSwitchedToLatestOnlyVersionFromLegacy then
                            // we are switching from a legacy version to one that is present only in the latest release
                            let result = 
                                VsShellUtilities.ShowMessageBox
                                    (
                                        serviceProvider = this.Site,
                                        message = FSharpSR.GetString(FSharpSR.FSharpCoreVersionIsNotLegacyCompatible),
                                        title = null,
                                        icon = OLEMSGICON.OLEMSGICON_QUERY, 
                                        msgButton = OLEMSGBUTTON.OLEMSGBUTTON_YESNO, 
                                        defaultButton = OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST
                                    )
                            if result <> NativeMethods.IDYES then
                                Marshal.ThrowExceptionForHR(VSConstants.OLE_E_PROMPTSAVECANCELLED)

                        this.SetProjectProperty(ProjectFileConstants.TargetFSharpCoreVersion, v)

                        let buildResult = this.Build(MsBuildTarget.ResolveAssemblyReferences);

                        for asmNode in System.Linq.Enumerable.OfType<AssemblyReferenceNode>(this.GetReferenceContainer().EnumReferences()) do
                            if (AssemblyReferenceNode.IsFSharpCoreReference asmNode) then
                                asmNode.RebindFSharpCoreAfterUpdatingVersion(buildResult)

                        this.UpdateTargetFramework(this.InteropSafeIVsHierarchy, this.GetTargetFrameworkMoniker(), this.GetTargetFrameworkMoniker()) |> ignore
                        this.ComputeSourcesAndFlags()
            
            override this.SendReferencesToFSI(references) = 
                Microsoft.VisualStudio.FSharp.Interactive.Hooks.AddReferencesToFSI this.Package references

            override x.SetSite(site:IOleServiceProvider) = 
                base.SetSite(site)  |> ignore
                
                let listener = new SolutionEventsListener(this)

                let buildMgr = this.Site.GetService(typeof<SVsSolutionBuildManager>) :?> IVsSolutionBuildManager
                if updateSolnEventsHandle <> 0u then
                    buildMgr.UnadviseUpdateSolutionEvents(updateSolnEventsHandle) |> ignore
                buildMgr.AdviseUpdateSolutionEvents((listener :> IVsUpdateSolutionEvents), &updateSolnEventsHandle) |> ignore
                let buildMgr2 = this.Site.GetService(typeof<SVsSolutionBuildManager>) :?> IVsSolutionBuildManager2
                if updateSolnEventsHandle2 <> 0u then
                    buildMgr2.UnadviseUpdateSolutionEvents(updateSolnEventsHandle2) |> ignore
                buildMgr2.AdviseUpdateSolutionEvents((listener :> IVsUpdateSolutionEvents2), &updateSolnEventsHandle2) |> ignore
                let buildMgr3 = this.Site.GetService(typeof<SVsSolutionBuildManager>) :?> IVsSolutionBuildManager3
                if updateSolnEventsHandle3 <> 0u then
                    buildMgr3.UnadviseUpdateSolutionEvents3(updateSolnEventsHandle3) |> ignore
                buildMgr3.AdviseUpdateSolutionEvents3((listener :> IVsUpdateSolutionEvents3), &updateSolnEventsHandle3) |> ignore
                let buildMgr5 = this.Site.GetService(typeof<SVsSolutionBuildManager>) :?> IVsSolutionBuildManager5
                if updateSolnEventsHandle4 <> 0u then
                    buildMgr5.UnadviseUpdateSolutionEvents4(updateSolnEventsHandle4) |> ignore
                buildMgr5.AdviseUpdateSolutionEvents4((listener :> IVsUpdateSolutionEvents4), &updateSolnEventsHandle4) |> ignore

                // Register for project retargeting events
                let sTrackProjectRetargeting = this.Site.GetService(typeof<SVsTrackProjectRetargeting>) :?> IVsTrackProjectRetargeting
                if trackProjectRetargetingCookie <> 0u then
                    sTrackProjectRetargeting.UnadviseTrackProjectRetargetingEvents(trackProjectRetargetingCookie) |> ignore
                sTrackProjectRetargeting.AdviseTrackProjectRetargetingEvents((listener :> IVsTrackProjectRetargetingEvents), &trackProjectRetargetingCookie) |> ignore

                isInCommandLineMode <-
                        let vsShell = this.Site.GetService(typeof<SVsShell>) :?> IVsShell
                        let mutable isInCommandLineMode : obj = null
                        vsShell.GetProperty(int __VSSPROPID.VSSPROPID_IsInCommandLineMode, &isInCommandLineMode) |> ignore
                        match isInCommandLineMode with
                        | :? bool as b -> b
                        | _ -> false
                accessor <- this.Site.GetService(typeof<SVsBuildManagerAccessor>) :?> IVsBuildManagerAccessor

                VSConstants.S_OK

            override x.Close() =
                projectSite.Close(x.CreateStaticProjectSite())
                
                let buildMgr = this.Site.GetService(typeof<SVsSolutionBuildManager>) :?> IVsSolutionBuildManager
                buildMgr.UnadviseUpdateSolutionEvents(updateSolnEventsHandle) |> ignore
                let buildMgr2 = this.Site.GetService(typeof<SVsSolutionBuildManager>) :?> IVsSolutionBuildManager2
                buildMgr2.UnadviseUpdateSolutionEvents(updateSolnEventsHandle2) |> ignore
                let buildMgr3 = this.Site.GetService(typeof<SVsSolutionBuildManager>) :?> IVsSolutionBuildManager3
                buildMgr3.UnadviseUpdateSolutionEvents3(updateSolnEventsHandle3) |> ignore
                let buildMgr5 = this.Site.GetService(typeof<SVsSolutionBuildManager>) :?> IVsSolutionBuildManager5
                buildMgr5.UnadviseUpdateSolutionEvents4(updateSolnEventsHandle4) |> ignore

                let documentTracker = this.Site.GetService(typeof<SVsTrackProjectDocuments>) :?> IVsTrackProjectDocuments2
                documentTracker.UnadviseTrackProjectDocumentsEvents(trackDocumentsHandle) |> ignore

                // Unregister for project retargeting events
                if trackProjectRetargetingCookie <> 0u then
                    let sTrackProjectRetargeting = this.Site.GetService(typeof<SVsTrackProjectRetargeting>) :?> IVsTrackProjectRetargeting
                    sTrackProjectRetargeting.UnadviseTrackProjectRetargetingEvents(trackProjectRetargetingCookie) |> ignore
                    trackProjectRetargetingCookie <- 0u

                if (null <> x.Site) then 
                    match TryGetService<IFSharpLibraryManager>(x.Site) with 
                    | Some(libraryManager) -> 
                        libraryManager.UnregisterHierarchy(this.InteropSafeIVsHierarchy)
                    | _ -> ()

                closeNotifier.Notify()
                vsProject <- null
                accessor <- null
                base.Close()

            override x.Load(filename:string, location:string, name:string, flags:uint32, iidProject:byref<Guid>, canceled:byref<int> ) =
                base.Load(filename, location, name, flags, &iidProject, &canceled)
                // WAP ask the designer service for the CodeDomProvider corresponding to the project node.
                this.OleServiceProvider.AddService(typeof<SVSMDCodeDomProvider>, new OleServiceProvider.ServiceCreatorCallback(this.CreateServices), false)
                this.OleServiceProvider.AddService(typeof<System.CodeDom.Compiler.CodeDomProvider>, new OleServiceProvider.ServiceCreatorCallback(this.CreateServices), false)

                match TryGetService<IFSharpLibraryManager>(x.Site) with 
                | Some(libraryManager) ->
                     libraryManager.RegisterHierarchy(this.InteropSafeIVsHierarchy)
                | _ -> ()

                // Listen for changes to files in the project
                let documentTracker = this.Site.GetService(typeof<SVsTrackProjectDocuments>) :?> IVsTrackProjectDocuments2
                documentTracker.AdviseTrackProjectDocumentsEvents(this, &trackDocumentsHandle) |> ignore

            /// Returns the outputfilename based on the output type
            member x.OutputFileName = 
                let assemblyName = this.ProjectMgr.GetProjectProperty(GeneralPropertyPageTag.AssemblyName.ToString(), true)

                let outputTypeAsString = this.ProjectMgr.GetProjectProperty(GeneralPropertyPageTag.OutputType.ToString(), false)
                let outputType = ParseEnum<OutputType>(outputTypeAsString) 

                assemblyName + GetOutputExtension(outputType)

            /// Get the VSProject corresponding to this project
            member this.VSProject : VSLangProj.VSProject  = 
                    if (vsProject= null) then 
                        vsProject <- (new OAVSProject(this) :> VSLangProj.VSProject)
                    vsProject
                        
            [<Conditional("DEBUG")>]
            member this.EnsureMSBuildAndSolutionExplorerAreInSync() =
                let AllSolutionExplorerFilenames() =
                    let rec Compute (node : HierarchyNode, accum) =
                        if obj.ReferenceEquals(node,null) then
                            accum
                        else
                            let mutable result = accum
                            match node with
                            | :? FSharpFolderNode -> ()
                            | :? FSharpFileNode -> result <- node.Caption ::accum
                            | _ -> Debug.Assert(false, "unexpected node type")
                            if not (obj.ReferenceEquals(node.FirstChild, null)) then
                                result <- Compute(node.FirstChild, result)
                            result <- Compute(node.NextSibling, result)
                            result
                    let node = this.FirstChild.NextSibling  // skip over 'References'
                    Compute(node, []) |> List.rev 
                let solnExplorer = AllSolutionExplorerFilenames()
                let msBuild = MSBuildUtilities.AllVisibleItemFilenames(this)
                Debug.Assert((solnExplorer = msBuild), sprintf "solution explorer view is out of sync with .fsproj file\n\nsolution explorer sees\n%A\n\nmsbuild sees\n%A" solnExplorer msBuild)

            static member ImageOffset = FSharpProjectNode.imageOffset 
            /// Since we appended the F# images to the base image list in the ctor,
            /// this should be the offset in the ImageList of the F# project icon.
            override x.ImageIndex = FSharpProjectNode.imageOffset + int32 FSharpImageName.FsProject

            override x.ProjectGuid = typeof<FSharpProjectFactory>.GUID
            override x.ProjectType = "FSharp"
            override x.Object = box this.VSProject
            
            // #region overridden methods
            override x.CreateReferenceContainerNode() : ReferenceContainerNode =
                new FSharpReferenceContainerNode(this) :> ReferenceContainerNode
                
            override x.CreateFolderNode(path, projectElement) =
                new FSharpFolderNode(x, path, projectElement) :> FolderNode
                
            override x.CreateFolderNodes(path) =
                base.CreateFolderNodes(path)

            override x.GetGuidProperty(propid:int, guid:byref<Guid> ) =
                if (enum propid = __VSHPROPID.VSHPROPID_PreferredLanguageSID) then 
                    guid <- new Guid(FSharpCommonConstants.languageServiceGuidString)
                    VSConstants.S_OK
                // below is how VS decide 'which templates' to associate with an 'add new item' call in this project
                elif (enum propid = __VSHPROPID2.VSHPROPID_AddItemTemplatesGuid) then 
                    guid <- typeof<FSharpProjectFactory>.GUID
                    VSConstants.S_OK
                else
                    base.GetGuidProperty(propid, &guid)

            member fshProjNode.MoveNewlyAddedFileSomehow<'a>(move : (*relativeFileName*)string -> unit, f : unit -> 'a) : 'a =
                Debug.Assert(addFilesNotification.IsNone, "bad use of addFilesNotification")
                addFilesNotification <- Some(fun files -> 
                    Debug.Assert(files.Length = 1)
                    let absoluteFileName = files.[0]
                    let relativeFileName = PackageUtilities.MakeRelativeIfRooted(absoluteFileName, fshProjNode.BaseURI)
                    move(relativeFileName))
                try
                    let r = f()
                    fshProjNode.ComputeSourcesAndFlags()
                    r
                finally
                    addFilesNotification <- None

            member fshProjNode.MoveNewlyAddedFileAbove<'a>(nodeToMoveAbove : HierarchyNode, f : unit -> 'a) : 'a =
                fshProjNode.MoveNewlyAddedFileSomehow((fun relativeFileName -> MSBuildUtilities.MoveFileAbove(relativeFileName, nodeToMoveAbove, fshProjNode)
                                                                               FSharpFileNode.MoveLastToAbove(nodeToMoveAbove, fshProjNode) |> ignore)
                                                      , f)

            member fshProjNode.MoveNewlyAddedFileBelow<'a>(nodeToMoveBelow : HierarchyNode, f : unit -> 'a) : 'a =
                fshProjNode.MoveNewlyAddedFileSomehow((fun relativeFileName -> MSBuildUtilities.MoveFileBelow(relativeFileName, nodeToMoveBelow, fshProjNode)
                                                                               FSharpFileNode.MoveLastToBelow(nodeToMoveBelow, fshProjNode) |> ignore)
                                                      , f)

            member fshProjNode.MoveNewlyAddedFileToBottomOfGroup<'a> (f : unit -> 'a) : 'a =
                fshProjNode.MoveNewlyAddedFileSomehow((fun relativeFileName -> MSBuildUtilities.MoveFileToBottomOfGroup(relativeFileName, fshProjNode)), f)

            override fshProjNode.MoveFileToBottomIfNoOtherPendingMove(relativeFileName) = 
                match addFilesNotification with
                | None -> MSBuildUtilities.MoveFileToBottomOfGroup(relativeFileName, fshProjNode)
                | Some _ -> ()

            override fshProjNode.ExecCommandOnNode(guidCmdGroup:Guid, cmd:uint32, nCmdexecopt:uint32, pvaIn:IntPtr, pvaOut:IntPtr ) =
                if guidCmdGroup = VsMenus.guidStandardCommandSet97 then
                    let cmdEnum : Microsoft.VisualStudio.VSConstants.VSStd97CmdID = enum (int cmd)
                    match cmdEnum with
                    | Microsoft.VisualStudio.VSConstants.VSStd97CmdID.AddNewItem ->
                        let r = fshProjNode.MoveNewlyAddedFileToBottomOfGroup (fun () ->
                            fshProjNode.AddItemToHierarchy(HierarchyAddType.AddNewItem))
                        fshProjNode.EnsureMSBuildAndSolutionExplorerAreInSync()
                        r

                    | Microsoft.VisualStudio.VSConstants.VSStd97CmdID.AddExistingItem ->
                        let r = fshProjNode.MoveNewlyAddedFileToBottomOfGroup (fun () ->
                            fshProjNode.AddItemToHierarchy(HierarchyAddType.AddExistingItem))
                        fshProjNode.EnsureMSBuildAndSolutionExplorerAreInSync()
                        r

                    | _ -> base.ExecCommandOnNode(guidCmdGroup, cmd, nCmdexecopt, pvaIn, pvaOut)
                elif guidCmdGroup = VsMenus.guidStandardCommandSet2K then
                    match (cmd |> int32 |> enum) : VSConstants.VSStd2KCmdID with 
                    | _ when cmd = MyVSConstants.ExploreFolderInWindows ->
                        System.Diagnostics.Process.Start("explorer.exe", fshProjNode.ProjectFolder) |> ignore
                        VSConstants.S_OK
                    | _ -> base.ExecCommandOnNode(guidCmdGroup, cmd, nCmdexecopt, pvaIn, pvaOut)
                else
                    base.ExecCommandOnNode(guidCmdGroup, cmd, nCmdexecopt, pvaIn, pvaOut)

            override fshProjNode.QueryStatusOnNode(guidCmdGroup : Guid, cmd : UInt32, pCmdText : IntPtr, result : byref<QueryStatusResult>) =
                if guidCmdGroup = VsMenus.guidStandardCommandSet97 then
                    if (cmd |> int32 |> enum) = Microsoft.VisualStudio.VSConstants.VSStd97CmdID.NewFolder then
                        result <- result ||| QueryStatusResult.SUPPORTED ||| QueryStatusResult.INVISIBLE 
                        VSConstants.S_OK
                    else
                        base.QueryStatusOnNode(guidCmdGroup, cmd, pCmdText, &result)
                elif guidCmdGroup = VsMenus.guidStandardCommandSet2K then
                    match (cmd |> int32 |> enum) : VSConstants.VSStd2KCmdID with 
                    | _ when cmd = MyVSConstants.ExploreFolderInWindows ->
                        result <- result ||| QueryStatusResult.SUPPORTED ||| QueryStatusResult.ENABLED
                        VSConstants.S_OK
                    | _ -> base.QueryStatusOnNode(guidCmdGroup, cmd, pCmdText, &result)
                else
                    base.QueryStatusOnNode(guidCmdGroup, cmd, pCmdText, &result)

            /// <summary>
            /// Used to sort nodes in the hierarchy.
            /// </summary>
            override fshProjNode.CompareNodes(node1 : HierarchyNode, node2 : HierarchyNode) =
                Debug.Assert(node1 <> null && node2 <> null)
                let IsFileOrFolder (n : HierarchyNode) =
                    match n with 
                    | :? FSharpFileNode -> true
                    | :? FSharpFolderNode -> true
                    | :? FileNode -> Debug.Assert(false, "FileNode that's not FSharpFileNode"); true
                    | :? FolderNode -> Debug.Assert(false, "FolderNode that's not FSharpFolderNode"); true
                    | _ -> false
                if node1.SortPriority = node2.SortPriority then
                    match IsFileOrFolder node1, IsFileOrFolder node2 with
                    | true, true -> 0  // all files and folders sort to same equivalence class - no auto-alphabetization
                    | _ -> String.Compare(node2.Caption, node1.Caption, true, CultureInfo.CurrentCulture)
                else
                    node2.SortPriority - node1.SortPriority

            override x.CreatePropertiesObject() : NodeProperties = 
                (new FSharpProjectNodeProperties(this) :> NodeProperties)

            /// Overriding to provide project general property page
            override x.GetConfigurationIndependentPropertyPages() =
                Array.append             
                    [|
                        typeof<FSharpApplicationPropPageComClass>.GUID 
                        typeof<FSharpBuildEventsPropPageComClass>.GUID 
                        typeof<FSharpReferencePathsPropPageComClass>.GUID 
                    |] (VSHiveUtilities.getCommonExtendedPropertyPages())

            /// Returns the configuration dependent property pages.
            override x.GetConfigurationDependentPropertyPages() =
                Array.append
                    [| 
                        typeof<FSharpBuildPropPageComClass>.GUID 
                        typeof<FSharpDebugPropPageComClass>.GUID 
                    |] (VSHiveUtilities.getConfigExtendedPropertyPages())
            
            /// Returns the property pages in a specific order        
            override x.GetPriorityProjectDesignerPages() =
                Array.append
                    [|
                        typeof<FSharpApplicationPropPageComClass>.GUID
                        typeof<FSharpBuildPropPageComClass>.GUID 
                        typeof<FSharpBuildEventsPropPageComClass>.GUID
                        typeof<FSharpDebugPropPageComClass>.GUID 
                        typeof<FSharpReferencePathsPropPageComClass>.GUID 
                    |] (VSHiveUtilities.getPriorityExtendedPropertyPages())

            /// Overriding to provide customization of files on add files.
            /// This will replace tokens in the file with actual value (namespace, class name,...)

            /// <param name="source">Full path to template file</param>
            /// <param name="target">Full path to destination file</param>
            override x.AddFileFromTemplate(source:string, target:string ) =
                if not (Microsoft.FSharp.Compiler.AbstractIL.Internal.Library.Shim.FileSystem.SafeExists(source)) then
                    raise <| new FileNotFoundException(String.Format(FSharpSR.GetString(FSharpSR.TemplateNotFound), source))

                // We assume that there is no token inside the file because the only
                // way to add a new element should be through the template wizard that
                // take care of expanding and replacing the tokens.
                // The only task to perform is to copy the source file in the
                // target location.
                let targetFolder = Path.GetDirectoryName(target)
                if not (Directory.Exists(targetFolder)) then 
                    Directory.CreateDirectory(targetFolder) |> ignore

                File.Copy(source, target)

            static member internal IsCompilingFSharpFile(strFileName:string ) =
                if (String.IsNullOrEmpty(strFileName)) then 
                    false
                else
                    // note that .fsx files do not compile in project
                     (String.Compare(Path.GetExtension(strFileName), ".fs", StringComparison.OrdinalIgnoreCase) = 0)
                  || (String.Compare(Path.GetExtension(strFileName), ".fsi", StringComparison.OrdinalIgnoreCase) = 0)
                  || (String.Compare(Path.GetExtension(strFileName), ".ml", StringComparison.OrdinalIgnoreCase) = 0)
                  || (String.Compare(Path.GetExtension(strFileName), ".mli", StringComparison.OrdinalIgnoreCase) = 0)

            static member internal IsFSharpCodeFileIconwise(strFileName:string ) =
                if (String.IsNullOrEmpty(strFileName)) then 
                    false
                else
                     (String.Compare(Path.GetExtension(strFileName), ".fs", StringComparison.OrdinalIgnoreCase) = 0)
                  || (String.Compare(Path.GetExtension(strFileName), ".ml", StringComparison.OrdinalIgnoreCase) = 0)

            static member internal IsFSharpSignatureFileIconwise(strFileName:string ) =
                if (String.IsNullOrEmpty(strFileName)) then 
                    false
                else
                     (String.Compare(Path.GetExtension(strFileName), ".fsi", StringComparison.OrdinalIgnoreCase) = 0)
                  || (String.Compare(Path.GetExtension(strFileName), ".mli", StringComparison.OrdinalIgnoreCase) = 0)

            static member internal IsFSharpScriptFileIconwise(strFileName:string ) =
                if (String.IsNullOrEmpty(strFileName)) then 
                    false
                else
                     (String.Compare(Path.GetExtension(strFileName), ".fsx", StringComparison.OrdinalIgnoreCase) = 0)
                  || (String.Compare(Path.GetExtension(strFileName), ".fsscript", StringComparison.OrdinalIgnoreCase) = 0)

            override x.DefaultBuildAction(strFileName:string ) =
                // Briefly, we just want out-of-the-box defaults to be like C#, without all their complicated logic, so we just hardcode a few values to be like C# and then otherwise default to NONE
                
                // Compile
                if FSharpProjectNode.IsCompilingFSharpFile strFileName then
                    ProjectFileConstants.Compile
                
                // EmbeddedResource
                elif (String.Compare(Path.GetExtension(strFileName), ".resx", StringComparison.OrdinalIgnoreCase) = 0) then
                    ProjectFileConstants.EmbeddedResource
                    
                // Content
                elif (String.Compare(Path.GetExtension(strFileName), ".bmp", StringComparison.OrdinalIgnoreCase) = 0) then
                    ProjectFileConstants.Content
                elif (String.Compare(Path.GetExtension(strFileName), ".cur", StringComparison.OrdinalIgnoreCase) = 0) then
                    ProjectFileConstants.Content
                elif (String.Compare(Path.GetExtension(strFileName), ".ico", StringComparison.OrdinalIgnoreCase) = 0) then
                    ProjectFileConstants.Content
                elif (String.Compare(Path.GetExtension(strFileName), ".js", StringComparison.OrdinalIgnoreCase) = 0) then
                    ProjectFileConstants.Content
                elif (String.Compare(Path.GetExtension(strFileName), ".txt", StringComparison.OrdinalIgnoreCase) = 0) then
                    ProjectFileConstants.Content
                elif (String.Compare(Path.GetExtension(strFileName), ".wsf", StringComparison.OrdinalIgnoreCase) = 0) then
                    ProjectFileConstants.Content
                elif (String.Compare(Path.GetExtension(strFileName), ".xml", StringComparison.OrdinalIgnoreCase) = 0) then
                    ProjectFileConstants.Content
                elif (String.Compare(Path.GetExtension(strFileName), ".css", StringComparison.OrdinalIgnoreCase) = 0) then
                    ProjectFileConstants.Content
                elif (String.Compare(Path.GetExtension(strFileName), ".config", StringComparison.OrdinalIgnoreCase) = 0) then
                    ProjectFileConstants.Content
                elif (String.Compare(Path.GetExtension(strFileName), ".map", StringComparison.OrdinalIgnoreCase) = 0) then
                    ProjectFileConstants.Content
                
                // None (including .fsx/.fsscript)
                else
                    ProjectFileConstants.None

            /// Create a file node based on an msbuild item.
            /// <param name="item">The msbuild item to be analyzed</param>
            /// <returns>FSharpFileNode</returns>
            override x.CreateFileNode(item:ProjectElement, hierarchyId : System.Nullable<uint32>) =
                if (item= null) then 
                    raise <| ArgumentNullException("item")

                let includ = item.GetMetadata(ProjectFileConstants.Include)
                let newNode = new FSharpFileNode(this, item, hierarchyId)
                newNode.OleServiceProvider.AddService(typeof<EnvDTE.Project>, new OleServiceProvider.ServiceCreatorCallback(this.CreateServices), false)
                newNode.OleServiceProvider.AddService(typeof<EnvDTE.ProjectItem>, newNode.ServiceCreator, false)
                newNode.OleServiceProvider.AddService(typeof<VSLangProj.VSProject>, new OleServiceProvider.ServiceCreatorCallback(this.CreateServices), false)
                if (FSharpProjectNode.IsCompilingFSharpFile(includ)) then 
                    newNode.OleServiceProvider.AddService(typeof<SVSMDCodeDomProvider>, new OleServiceProvider.ServiceCreatorCallback(this.CreateServices), false)

                (newNode :> LinkedFileNode)

            /// Creates the format list for the open file dialog
            /// <param name="formatlist">The formatlist to return</param>
            override x.GetFormatList(formatlist:byref<string> ) =
                // see docs for IPersistFileFormat.GetFormatList for correct format of this string
                formatlist <- sprintf "%s\n*.fsproj\n" (FSharpSR.GetString(FSharpSR.ProjectFileExtensionFilter))
                VSConstants.S_OK

            member this.IsCurrentProjectDotNetPortable() = this.CheckProjectFrameworkIdentifier(".NETPortable")
            member this.IsCurrentProjectSilverlight() = this.CheckProjectFrameworkIdentifier("Silverlight")

            interface IVsFilterAddProjectItemDlg with
                member filter.FilterListItemByLocalizedName(_rguidProjectItemTemplates, _pszLocalizedName, _pfFilter) = 
                    VSConstants.S_OK
                member filter.FilterListItemByTemplateFile(_rguidProjectItemTemplates, pszLocalizedName, pfFilter) = 
                    pfFilter <- if (this.IsCurrentProjectDotNetPortable() || this.IsCurrentProjectSilverlight()) && pszLocalizedName.Contains(@"\FSharp\FSharpData\") then 1 else 0
                    VSConstants.S_OK
                member filter.FilterTreeItemByLocalizedName(_rguidProjectItemTemplates, _pszLocalizedName, _pfFilter) = 
                    VSConstants.S_OK
                member filter.FilterTreeItemByTemplateDir(_rguidProjectItemTemplates, pszTemplateDir, pfFilter) = 
                    pfFilter <- if (this.IsCurrentProjectDotNetPortable() || this.IsCurrentProjectSilverlight()) && pszTemplateDir = @"FSharp\FSharpData" then 1 else 0
                    VSConstants.S_OK

            /// This overrides the base class method to show the VS 2005 style Add reference dialog. The ProjectNode implementation
            /// shows the VS 2003 style Add Reference dialog.
            override self.AddProjectReference() =

                //Add the .NET page

                // To properly filter out references, we need to get the target framework moniker
                // and then get the right set of directories for the varTabInitInfo field.
                // The native project equivalent of this code is in vsproject\langproj\langrefnode.cpp.

                let stripEndingSemicolon (sb : StringBuilder) =
                    let len = sb.Length
                    if sb.[len - 1] = ';' then sb.Remove(len - 1, 1) |> ignore
                    ()

                let targetFrameworkMoniker = this.GetTargetFrameworkMoniker()
                let enumComponents = this.Site.GetService(typeof<SCompEnumService>) :?> IVsComponentEnumeratorFactory4
                let (hr,componentEnumerator) = enumComponents.GetReferencePathsForTargetFramework(targetFrameworkMoniker)
                let paths = new StringBuilder(1024)
                if hr = VSConstants.S_OK then
                    let mutable selData = Array.zeroCreate<VSCOMPONENTSELECTORDATA> 1
                    let mutable fetched = 0u
                    while componentEnumerator.Next(1u, selData, &fetched) = VSConstants.S_OK && fetched = 1u do
                        let _ = paths.Append(selData.[0].bstrFile + ";")
                        ()
                    stripEndingSemicolon paths

                let dialogTitle = 
                    let text = FSharpSR.GetString(FSharpSR.AddReferenceDialogTitleDev11)
                    String.Format(text, self.VSProject.Project.Name)

                let referenceContainerNode = this.GetReferenceContainer() :?> ReferenceContainerNode
                let componentDialog = this.Site.GetService(typeof<SVsReferenceManager>) :?> IVsReferenceManager
                let providers() =  [|
                        // assembly references
                    let c = 
                        let c = componentDialog.CreateProviderContext(VSConstants.AssemblyReferenceProvider_Guid)

                        let assemblyReferenceProviderContext = c :?> IVsAssemblyReferenceProviderContext 

                        assemblyReferenceProviderContext.TargetFrameworkMoniker <- targetFrameworkMoniker
                        assemblyReferenceProviderContext.SupportsRetargeting <- true
                        assemblyReferenceProviderContext.AssemblySearchPaths <- this.GetProjectProperty("AssemblySearchPaths")
                        for r in referenceContainerNode.EnumReferences() do
                            match r with
                            | :? AssemblyReferenceNode as arn ->
                                let newRef = assemblyReferenceProviderContext.CreateReference() :?> IVsAssemblyReference
                                assemblyReferenceProviderContext.AddReference(newRef)
                                newRef.Name <- arn.SimpleName 
                                newRef.FullPath <- arn.Url 
                            | _ -> ()
                        
                        let existingFilter = 
                            match assemblyReferenceProviderContext.ReferenceFilterPaths with
                            | null -> ResizeArray()
                            | x -> ResizeArray(x :?> string[])

                        if self.IsCurrentProjectDotNetPortable() then
                            // for portable projects AddReference dialog should be adjusted
                            // taken from vsproject\flavors\portable\package\microsoft.visualstudio.portablelibrary\microsoft\visualstudio\portablelibrary\projectflavoring\referencing\providers\assemblyreferenceprovidercontextprovider.cs

                            // MSDN: Gets or sets whether the assembly is referenced implicitly
                            assemblyReferenceProviderContext.IsImplicitlyReferenced <- false
                            // MSDN: Gets or sets the message to display during retargeting.
                            assemblyReferenceProviderContext.RetargetingMessage <- FSharpSR.GetString(FSharpSR.AddReferenceAssemblyPageDialogRetargetingText)
                            // MSDN: Sets the custom no items message for the specified tab.
                            assemblyReferenceProviderContext.SetNoItemsMessageForTab(uint32 __VSASSEMBLYPROVIDERTAB.TAB_ASSEMBLY_FRAMEWORK, FSharpSR.GetString(FSharpSR.AddReferenceAssemblyPageDialogNoItemsText))
                            // we support only fixed set of portable profiles thus retargeting is prohibited
                            assemblyReferenceProviderContext.SupportsRetargeting <- false

                            if self.ImplicitlyExpandTargetFramework then

                                // since all framework references are included implicitly we should hide them from the Framework page to disallow adding them one more time
                                // to do this we'll obtain assembly location for the current framework and add it to IVsAssemblyReferenceProviderContext.ReferenceFilterPaths
                                let multiTargetingService = this.GetService(typeof<SVsFrameworkMultiTargeting>) :?> IVsFrameworkMultiTargeting

                                let frameworkName = self.GetCurrentFrameworkName()
                                let mutable frameworkAssemblies = Unchecked.defaultof<_>

                                multiTargetingService.GetFrameworkAssemblies(frameworkName.FullName, uint32 __VSFRAMEWORKASSEMBLYTYPE.VSFRAMEWORKASSEMBLYTYPE_FRAMEWORK, &frameworkAssemblies)
                                |> (ErrorHandler.ThrowOnFailure >> ignore)

                                if frameworkAssemblies.Length <> 0 then 
                                    let path = Path.GetDirectoryName (frameworkAssemblies.GetValue(0) :?> string) // all assemblies in the profile are located in the same place - just pick the first one
                                    existingFilter.Add(path)
                        assemblyReferenceProviderContext.ReferenceFilterPaths <- existingFilter.ToArray()
                        c
                    yield c
                    // project references
                    let c = 
                        let c = componentDialog.CreateProviderContext(VSConstants.ProjectReferenceProvider_Guid)
                        let projectReferenceProviderContext = c :?> IVsProjectReferenceProviderContext 
                        projectReferenceProviderContext.CurrentProject <- this.InteropSafeIVsHierarchy
                        for r in referenceContainerNode.EnumReferences() do
                            match r with
                            | :? ProjectReferenceNode as prn ->
                                let newRef = projectReferenceProviderContext.CreateReference() :?> IVsProjectReference
                                projectReferenceProviderContext.AddReference(newRef)
                                newRef.Identity <- prn.ReferencedProjectGuid.ToString("B")
                            | _ -> ()
                        c
                    yield c
                    if not(self.IsCurrentProjectDotNetPortable()) then  // Portable libraries should not show COM reference tab in add-ref dialog
                        // COM references
                        let c = 
                            let c = componentDialog.CreateProviderContext(VSConstants.ComReferenceProvider_Guid)
                            let comReferenceProviderContext = c :?> IVsComReferenceProviderContext 
                            for r in referenceContainerNode.EnumReferences() do
                                match r with
                                | :? ComReferenceNode as crn ->
                                    let newRef = comReferenceProviderContext.CreateReference() :?> IVsComReference
                                    comReferenceProviderContext.AddReference(newRef)
                                    // Identity apparently should be this form:  {guid}\major.minor
                                    newRef.Identity <- sprintf "%s\\%x.%x" (crn.TypeGuid.ToString("B")) crn.MajorVersionNumber crn.MinorVersionNumber
                                | _ -> ()
                            c
                        yield c
                    // Browse provider
                    let c = 
                        let c = componentDialog.CreateProviderContext(VSConstants.FileReferenceProvider_Guid)
                        let fileReferenceProviderContext = c :?> IVsFileReferenceProviderContext 
                        fileReferenceProviderContext.BrowseFilter <- sprintf "%s|*.dll;*.exe;" (FSharpSR.GetString FSharpSR.ComponentFileExtensionFilter)
                        c
                    yield c
                    // TODO, eventually, win8 stuff
                    |]
                let user = 
                    { new IVsReferenceManagerUser with
                        member this.GetProviderContexts() = 
                            providers() :> System.Array
                        member this.ChangeReferences(op, ctxt) =
                            let mutable returnVal = __VSREFERENCECHANGEOPERATIONRESULT.VSREFERENCECHANGEOPERATIONRESULT_DENY 
                            if op = (uint32)__VSREFERENCECHANGEOPERATION.VSREFERENCECHANGEOPERATION_ADD then
                                let data = 
                                    if ctxt.ProviderGuid = VSConstants.AssemblyReferenceProvider_Guid then
                                        // add assembly references
                                        let assemblyReferenceProviderContext = ctxt :?> IVsAssemblyReferenceProviderContext 

                                        let newTargetFrameworkMoniker = assemblyReferenceProviderContext.TargetFrameworkMoniker
                                        if newTargetFrameworkMoniker <> targetFrameworkMoniker then
                                            // They added an assembly that requires retargeting.  Retarget the project.
                                            self.UpdateTargetFramework(self.InteropSafeIVsHierarchy, targetFrameworkMoniker, newTargetFrameworkMoniker) |> ignore
                                            self.SetProperty(VSConstants.VSITEMID_ROOT, int32 __VSHPROPID4.VSHPROPID_TargetFrameworkMoniker, newTargetFrameworkMoniker) |> ignore

                                        assemblyReferenceProviderContext.References 
                                        |> Seq.cast |> Seq.map (fun (ar : IVsAssemblyReference) ->
                                            let mutable datum = new VSCOMPONENTSELECTORDATA()
                                            datum.dwSize <- uint32( Marshal.SizeOf(typeof<VSCOMPONENTSELECTORDATA>) )
                                            datum.``type`` <- VSCOMPONENTTYPE.VSCOMPONENTTYPE_ComPlus
                                            datum.bstrTitle <- ar.Name 
                                            datum.bstrFile <- ar.FullPath 
                                            datum
                                            )
                                        |> Seq.toArray 
                                    elif ctxt.ProviderGuid = VSConstants.ProjectReferenceProvider_Guid then
                                        // add project references
                                        let projectReferenceProviderContext = ctxt :?> IVsProjectReferenceProviderContext 
                                        projectReferenceProviderContext.References 
                                        |> Seq.cast |> Seq.map (fun (pr : IVsProjectReference) ->
                                            let mutable datum = new VSCOMPONENTSELECTORDATA()
                                            datum.dwSize <- uint32( Marshal.SizeOf(typeof<VSCOMPONENTSELECTORDATA>) )
                                            datum.``type`` <- VSCOMPONENTTYPE.VSCOMPONENTTYPE_Project 
                                            datum.bstrTitle <- pr.Name 
                                            datum.bstrFile <- pr.FullPath 
                                            datum.bstrProjRef <- pr.ReferenceSpecification 
                                            datum
                                            )
                                        |> Seq.toArray 
                                    elif ctxt.ProviderGuid = VSConstants.ComReferenceProvider_Guid then
                                        // add COM references
                                        let comReferenceProviderContext = ctxt :?> IVsComReferenceProviderContext 
                                        comReferenceProviderContext.References 
                                        |> Seq.cast |> Seq.map (fun (cr : IVsComReference) ->
                                            let mutable datum = new VSCOMPONENTSELECTORDATA()
                                            datum.dwSize <- uint32( Marshal.SizeOf(typeof<VSCOMPONENTSELECTORDATA>) )
                                            datum.``type`` <- VSCOMPONENTTYPE.VSCOMPONENTTYPE_Com2 
                                            datum.bstrTitle <- cr.Name 
                                            datum.bstrFile <- cr.FullPath 
                                            datum.guidTypeLibrary <- cr.Guid 
                                            datum.wTypeLibraryMajorVersion <- cr.MajorVersion 
                                            datum.wTypeLibraryMinorVersion <- cr.MinorVersion 
                                            datum
                                            )
                                        |> Seq.toArray 
                                    elif ctxt.ProviderGuid = VSConstants.FileReferenceProvider_Guid then
                                        // add browsed-file references
                                        let fileReferenceProviderContext = ctxt :?> IVsFileReferenceProviderContext 
                                        fileReferenceProviderContext.References 
                                        |> Seq.cast |> Seq.map (fun (fr : IVsFileReference) ->
                                            let mutable datum = new VSCOMPONENTSELECTORDATA()
                                            datum.dwSize <- uint32( Marshal.SizeOf(typeof<VSCOMPONENTSELECTORDATA>) )
                                            datum.``type`` <- VSCOMPONENTTYPE.VSCOMPONENTTYPE_File
                                            datum.bstrFile <- fr.FullPath 
                                            datum
                                            )
                                        |> Seq.toArray 
                                    // TODO, eventually win8 stuff
                                    else [| |]
                                let dataPtrs = Array.init data.Length (fun i -> 
                                    let p = Marshal.AllocHGlobal(Marshal.SizeOf(typeof<VSCOMPONENTSELECTORDATA>))
                                    Marshal.StructureToPtr(data.[i], p, false)
                                    p)
                                try
                                    let result = [| VSADDCOMPRESULT() |]
                                    self.BeginBatchUpdate()
                                    let hr = self.AddComponent(VSADDCOMPOPERATION.VSADDCOMPOP_ADD, uint32 data.Length, dataPtrs, System.IntPtr.Zero, result)
                                    if Microsoft.VisualStudio.ErrorHandler.Succeeded(hr) && result.[0] = VSADDCOMPRESULT.ADDCOMPRESULT_Success then
                                        returnVal <- __VSREFERENCECHANGEOPERATIONRESULT.VSREFERENCECHANGEOPERATIONRESULT_ALLOW                                        
                                finally
                                    for p in dataPtrs do
                                        Marshal.DestroyStructure(p, typeof<VSCOMPONENTSELECTORDATA>)
                                    self.EndBatchUpdate()
                            elif op = (uint32)__VSREFERENCECHANGEOPERATION.VSREFERENCECHANGEOPERATION_REMOVE then
                                if ctxt.ProviderGuid = VSConstants.AssemblyReferenceProvider_Guid || ctxt.ProviderGuid = VSConstants.FileReferenceProvider_Guid then
                                    // remove assembly references
                                    let references = 
                                        match ctxt with
                                        | :? IVsAssemblyReferenceProviderContext as ctxt -> [| for r in ctxt.References |> Seq.cast<IVsAssemblyReference> -> r.FullPath |]
                                        | :? IVsFileReferenceProviderContext as ctxt -> [| for r in ctxt.References |> Seq.cast<IVsFileReference> -> r.FullPath |]
                                        | _ -> System.Diagnostics.Debug.Assert(false, "Mismatched ctxt type, should not ever happen"); [||]

                                    let nodes = [|
                                        for toRemove in references do
                                            for r in referenceContainerNode.EnumReferences() do
                                                match r with
                                                | :? AssemblyReferenceNode as arn ->
                                                    if arn.Url.ToLower() = toRemove.ToLower() then
                                                        yield arn
                                                | _ -> () |]
                                    if nodes.Length = references.Length then
                                        try
                                            self.BeginBatchUpdate()
                                            for node in nodes do
                                                node.Remove(false)
                                        finally
                                            self.EndBatchUpdate()
                                        returnVal <- __VSREFERENCECHANGEOPERATIONRESULT.VSREFERENCECHANGEOPERATIONRESULT_ALLOW
                                    else
                                        System.Diagnostics.Debug.Assert(false, "remove assembly\file reference, when would this happen?")

                                elif ctxt.ProviderGuid = VSConstants.ProjectReferenceProvider_Guid then
                                    // remove project references
                                    let projectReferenceProviderContext = ctxt :?> IVsProjectReferenceProviderContext 
                                    let nodes = [|
                                        for toRemove in Seq.cast<IVsProjectReference> projectReferenceProviderContext.References do
                                            for r in referenceContainerNode.EnumReferences() do
                                                match r with
                                                | :? ProjectReferenceNode as prn ->
                                                    if prn.ReferencedProjectGuid.ToString("B").ToLower() = toRemove.Identity.ToLower() then
                                                        yield prn
                                                | _ -> () |]
                                    if nodes.Length = projectReferenceProviderContext.References.Length then
                                        try
                                            self.BeginBatchUpdate()
                                            for node in nodes do
                                                node.Remove(false)
                                        finally
                                            self.EndBatchUpdate()
                                        returnVal <- __VSREFERENCECHANGEOPERATIONRESULT.VSREFERENCECHANGEOPERATIONRESULT_ALLOW
                                    else
                                        System.Diagnostics.Debug.Assert(false, "remove project reference, when would this happen?")
                                elif ctxt.ProviderGuid = VSConstants.ComReferenceProvider_Guid then
                                    // remove COM references
                                    let comReferenceProviderContext = ctxt :?> IVsComReferenceProviderContext 
                                    let nodes = [|
                                        for toRemove in Seq.cast<IVsComReference> comReferenceProviderContext.References do
                                            for r in referenceContainerNode.EnumReferences() do
                                                match r with
                                                | :? ComReferenceNode as crn ->
                                                    if crn.TypeGuid = toRemove.Guid then
                                                        yield crn
                                                | _ -> () |]
                                    if nodes.Length = comReferenceProviderContext.References.Length then
                                        try
                                            self.BeginBatchUpdate()
                                            for node in nodes do
                                                node.Remove(false)
                                        finally
                                            self.EndBatchUpdate()
                                        returnVal <- __VSREFERENCECHANGEOPERATIONRESULT.VSREFERENCECHANGEOPERATIONRESULT_ALLOW
                                    else
                                        System.Diagnostics.Debug.Assert(false, "remove COM reference, when would this happen?")
                                // TODO, eventually win8 stuff
                            match returnVal with
                            | __VSREFERENCECHANGEOPERATIONRESULT.VSREFERENCECHANGEOPERATIONRESULT_ALLOW -> ()
                            | _ -> raise (new System.Runtime.InteropServices.COMException("", VSConstants.E_ABORT))
                            }
                try
                    // Let the project know not to show itself in the Add Project Reference Dialog page
                    this.ShowProjectInSolutionPage <- false
                    let providerContexts = providers()
                    componentDialog.ShowReferenceManager(user, dialogTitle, "VS.ReferenceManager" (*Help topic*), providerContexts.[0].ProviderGuid, false (* Don't force a switch to the default provider *))
                    VSConstants.S_OK
                finally
                    // Let the project know it can show itself in the Add Project Reference Dialog page
                    this.ShowProjectInSolutionPage <- true

            override x.CreateConfigProvider() = new ConfigProvider(this)
            
            /// Creates the services exposed by this project.
            member x.CreateServices(serviceType:Type) =
                if (typeof<VSLangProj.VSProject> = serviceType) then 
                    this.VSProject |> box
                else if (typeof<EnvDTE.Project> = serviceType) then 
                    this.GetAutomationObject()
                else 
                    null
                    
            override x.Save(fileToBeSaved, remember, formatIndex) =
                let r = base.Save(fileToBeSaved, remember, formatIndex)
                x.ComputeSourcesAndFlags()
                r
                
            override x.DoMSBuildSubmission(buildKind, target, projectInstance, uiThreadCallback, extraProperties) =
                if (String.Compare(target,"clean",true)=0) || (String.Compare(target,"rebuild",true)=0) then  // MSBuild targets are case-insensitive
                    cleanNotifier.Notify()
                base.DoMSBuildSubmission(buildKind, target, &projectInstance, uiThreadCallback, extraProperties)

            override x.InvokeMsBuild(target, extraProperties) =
                let result = base.InvokeMsBuild(target, extraProperties)
                result

            // Fulfill HostObject contract with Fsc task, and enable 'capture' of compiler flags for the project.
#if FX_NO_CONVERTER
            member x.Compile(compile:Func<int>, flags:string[], sources:string[]) = 
#else
            member x.Compile(compile:System.Converter<int,int>, flags:string[], sources:string[]) = 
#endif
                // Note: This method may be called from non-UI thread!  The Fsc task in FSharp.Build.dll invokes this method via reflection, and
                // the Fsc task is typically created by MSBuild on a background thread.  So be careful.
#if DEBUG
                compileWasActuallyCalled <- true
#endif                    
                let normalizedSources = sources |> Array.map (fun fn -> System.IO.Path.GetFullPath(System.IO.Path.Combine(x.ProjectFolder, fn)))
                let r = (normalizedSources, flags)
                sourcesAndFlags <- Some(r)
                if projectSite.State = ProjectSiteOptionLifetimeState.Opening then
                    // This is the first time, so set up interface for language service to talk to us
                    projectSite.Open(x.CreateRunningProjectSite())
                if actuallyBuild then
#if FX_NO_CONVERTER
                    compile.Invoke()
#else
                    compile.Invoke(0)
#endif
                else
                    0

            // returns an array of all "foo"s of form: <Compile Include="foo"/>
            member private x.ComputeCompileItems() =
                FSharpProjectNode.ComputeCompileItems(x.BuildProject, x.ProjectFolder)
            static member ComputeCompileItems(buildProject, projectFolder) =
                [|
                for i in buildProject.Items do
                    if i.ItemType = "Compile" then
                        yield System.IO.Path.GetFullPath(System.IO.Path.Combine(projectFolder, i.EvaluatedInclude))
                |]
            member x.GetCompileItems() = let sources,_ = sourcesAndFlags.Value in sources
            member x.GetCompileFlags() = let _,flags = sourcesAndFlags.Value in flags

            override x.ComputeSourcesAndFlags() =

                if x.IsInBatchUpdate || box x.BuildProject = null then ()
                else
                if not(inMidstOfReloading) && not(VsBuildManagerAccessorExtensionMethods.IsInProgress(accessor)) then

                    use waitDialog =
                        {
                            WaitCaption = FSharpSR.GetString FSharpSR.ProductName
                            WaitMessage = FSharpSR.GetString FSharpSR.ComputingSourcesAndFlags
                            ProgressText = Some x.ProjectFile
                            StatusBmpAnim = null
                            StatusBarText = None
                            DelayToShowDialogSecs = 1
                            IsCancelable = false
                            ShowMarqueeProgress = true
                        }
                        |> WaitDialog.start x.Site
                
                    // REVIEW CompilerFlags will be stale since last 'save' of MSBuild .fsproj file - can we do better?
                    try
                        actuallyBuild <- false 
                        x.SetCurrentConfiguration()

                        // Only set this property when building within VS Proper - not within the unit tests (UnitTestingFSharpProjectNode)
                        if x.GetType() = typeof<FSharpProjectNode> then
                            MSBuildProject.SetGlobalProperty(x.BuildProject, "UTF8Output", "true")

#if DEBUG
                        compileWasActuallyCalled <- false
                        x.SetDebugLogger(logger)
#endif
                        // TODO: check whether this can be achieved in a less painful way 
                        // (names that start with '_' are treated as implementation details and not recommended for usage).
                        // Setting this property enforces msbuild to resolve second order dependencies,
                        // so if desktop applications references .NETCore based portable assemblies
                        // then MSBuild will detect that one of references requires System.Runtime and 
                        // expand list of references with framework facades for portables.
                        // Usually facades are located at:L<Program Files>Reference Assemblies\Microsoft\Framework\.NETFramework\v4.5\Facades\
                        // If property is not set - msbuild will resolve only primary dependencies,
                        // and compiler will be very unhappy when during processing of referenced assembly it will discover that all fundamental types should be
                        // taken from System.Runtime that is not supplied
                        
                        let _ = x.InvokeMsBuild("Compile", extraProperties = [KeyValuePair("_ResolveReferenceDependencies", "true")])
                        sourcesAndFlagsNotifier.Notify()
                    finally
                        actuallyBuild <- true

            member internal x.DetermineRuntimeAndSKU(targetFrameworkMoniker : string) =
                let frameworkName = new System.Runtime.Versioning.FrameworkName(targetFrameworkMoniker)
                let runtime = if frameworkName.Version.Major >= 4 then "v4.0" else "v2.0.50727"
                let sku = if frameworkName.Version.Major < 4 then frameworkName.Profile
                          else 
                              let mtservice = this.GetService(typeof<SVsFrameworkMultiTargeting>) :?> IVsFrameworkMultiTargeting
                              let (_, res) = mtservice.GetInstallableFrameworkForTargetFx(targetFrameworkMoniker)
                              res

                (runtime, if String.IsNullOrEmpty(sku) then null else sku)

            member internal x.DoFixupAppConfigOnTargetFXChange(runtime : string, sku : string, targetFSharpCoreVersion : string, autoGenerateBindingRedirects : bool ) =
                let mutable res = VSConstants.E_FAIL
                let specialFiles = x :> IVsProjectSpecialFiles
                // We only want to force-generate an AppConfig file if the output type is EXE;
                // presence of app.config is required to handle 'binding redirect' issues
                let projProp = new FSharpProjectNodeProperties(x)
                let dwFlags = 
                    if projProp.OutputType = OutputType.WinExe || projProp.OutputType = OutputType.Exe then
                        __PSFFLAGS.PSFF_FullPath ||| __PSFFLAGS.PSFF_CreateIfNotExist
                    else
                        __PSFFLAGS.PSFF_FullPath

                let (hr, itemid, _filename) = specialFiles.GetFile(int (__PSFFILEID.PSFFILEID_AppConfig), uint32 dwFlags)
                res <- hr
                if ErrorHandler.Succeeded(res) then
                    let langConfigFile = new LangConfigFile(x.Site)
                    try
                        res <- langConfigFile.Open(x)
                        if ErrorHandler.Succeeded(res) then
                            langConfigFile.EnsureSupportedRuntimeElement(runtime, sku)
                            let node = x.NodeFromItemId(itemid)
                            System.Diagnostics.Debug.Assert(node <> null, "No project node for the item?")
                            if node <> null then
                                langConfigFile.EnsureHasBindingRedirects(targetFSharpCoreVersion, autoGenerateBindingRedirects)
                                res <- langConfigFile.Save()

                        // if we couldn't find the file, but we don't need it, then just ignore
                        if projProp.OutputType = OutputType.Library && res = NativeMethods.STG_E_FILENOTFOUND then
                            res <- VSConstants.S_OK
                    finally
                        (langConfigFile :> IDisposable).Dispose()
                res

            override x.FixupAppConfigOnTargetFXChange(targetFrameworkMoniker, targetFSharpCoreVersion, autoGenerateBindingRedirects) =
                let (runtime, sku) = x.DetermineRuntimeAndSKU(targetFrameworkMoniker)
                x.DoFixupAppConfigOnTargetFXChange(runtime, sku, targetFSharpCoreVersion, autoGenerateBindingRedirects)

            override x.SetHostObject(targetName, taskName, hostObject) =
                base.SetHostObject(targetName, taskName, hostObject)
                
            override x.SetBuildProject newProj =
                base.SetBuildProject newProj
                if x.BuildProject <> null then
                    x.SetHostObject("CoreCompile", "Fsc", this) |> ignore
                    
            override x.Reload() =
                inMidstOfReloading <- true
                try
                    base.Reload()
                    inMidstOfReloading <- false
                    if this.IsProjectOpened then // Protect against calling CSAF on non-opened project
                        x.ComputeSourcesAndFlags()
                    MSBuildUtilities.ThrowIfNotValidAndRearrangeIfNecessary x
                finally
                    inMidstOfReloading <- false

            // Returns an IProjectSite that references "this" to get its information
            member private x.CreateRunningProjectSite() =
                let creationTime = System.DateTime.Now 
                { new Microsoft.VisualStudio.FSharp.LanguageService.IProjectSite with
                    // Note: these methods get called by language service from an arbitrary thread, but the things they
                    // access (x.GetCompileItems, x.Caption, sourcesAndFlags) are all thread-safe.
                    member ips.SourceFilesOnDisk() = x.GetCompileItems()
                    member ips.DescriptionOfProject() = 
                        let sources,flags = sourcesAndFlags.Value
                        sprintf "Project System: flags(%A) sources:\n%A" flags sources
                    member ips.CompilerFlags() = x.GetCompileFlags()
                    member ips.ProjectFileName() = MSBuildProject.GetFullPath(x.BuildProject)
                    member ips.ErrorListTaskProvider() = Some(x.TaskProvider)
                    member ips.ErrorListTaskReporter() = Some(x.TaskReporter)
                    member this.AdviseProjectSiteChanges(callbackOwnerKey,callback) =
                        sourcesAndFlagsNotifier.Advise(callbackOwnerKey,callback)
                    member this.AdviseProjectSiteCleaned(callbackOwnerKey,callback) =
                        cleanNotifier.Advise(callbackOwnerKey,callback)
                    member this.AdviseProjectSiteClosed(callbackOwnerKey,callback) =
                        closeNotifier.Advise(callbackOwnerKey,callback)
                    member this.IsIncompleteTypeCheckEnvironment = false
                    member this.TargetFrameworkMoniker = x.GetTargetFrameworkMoniker()
                    member this.ProjectGuid = x.GetProjectGuid()
                    member this.LoadTime = creationTime
                    member this.ProjectProvider = Some (x :> IProvideProjectSite)
                }

            // Snapshot-capture relevent values from "this", and returns an IProjectSite 
            // that does _not_ reference "this" to get its information.
            member private x.CreateStaticProjectSite() =
                // CreateStaticProjectSite can be called on a project that failed to load (as in Close)
                let compileItems,flags =                                     
                    match sourcesAndFlags with
                    |   None -> Array.create 0 "", Array.create 0 ""
                    |   Some(sources,flags) -> sources, flags
                let caption = x.Caption
                let projFileName = MSBuildProject.GetFullPath(x.BuildProject)
                let taskProvider = Some(x.TaskProvider)
                let taskReporter = Some(x.TaskReporter)
                let targetFrameworkMoniker = x.GetTargetFrameworkMoniker()
                let creationTime = System.DateTime.Now 
                // This object is thread-safe
                { new Microsoft.VisualStudio.FSharp.LanguageService.IProjectSite with
                    member ips.SourceFilesOnDisk() = compileItems
                    member ips.DescriptionOfProject() = caption
                    member ips.CompilerFlags() = flags
                    member ips.ProjectFileName() = projFileName
                    member ips.ErrorListTaskProvider() = taskProvider
                    member ips.ErrorListTaskReporter() = taskReporter
                    member this.AdviseProjectSiteChanges(_,_) = ()
                    member this.AdviseProjectSiteCleaned(_,_) = ()
                    member this.AdviseProjectSiteClosed(_,_) = ()
                    member this.IsIncompleteTypeCheckEnvironment = false
                    member this.TargetFrameworkMoniker = targetFrameworkMoniker
                    member this.ProjectGuid = x.GetProjectGuid()
                    member this.LoadTime = creationTime
                    member this.ProjectProvider = Some (x :> IProvideProjectSite)
                }

            // let the language service ask us questions
            interface Microsoft.VisualStudio.FSharp.LanguageService.IProvideProjectSite with
                member x.GetProjectSite() = 
                    match projectSite.State with
                    | ProjectSiteOptionLifetimeState.Opening ->
#if DEBUG                    
                        Debug.Assert(System.Threading.Thread.CurrentThread.ManagedThreadId = uiThreadId, "called GetProjectSite() while still in Opening state from non-UI thread")
#endif                        
                        x.ComputeSourcesAndFlags()
                        if not(projectSite.State = ProjectSiteOptionLifetimeState.Opened) then
                            // We failed to Build.  This can happen e.g. when the user has custom MSBuild functionality under the "Compile" target, e.g. a CompileDependsOn that fails.
                            // We now have no reliable way to get information about the project.
                            // Rather than be in a completely useless state, we just report 0 source files and 0 compiler flags.
                            // This keeps the PS 'alive', allows opening individual files in the editor, etc.
                            // If the user clicks e.g. "Build", he will see an error diagnostic in the error list that will help him out.
                            // Once that error is fixed, a future call to ComputeSourcesAndFlags() will successfully call through to our HostObject and get to Compile(), 
                            // which will finally populate sourcesAndFlags with good values.
                            // This means that ones the user fixes the problem, proper intellisense etc. should start immediately lighting up.
                            sourcesAndFlags <- Some([||],[||])
                            projectSite.Open(x.CreateRunningProjectSite())
                        ()
                    | _ -> ()
                    projectSite.GetProjectSite()

            member x.OneOfTheProjectsIsThisOne(cProjects, rgpProjects : IVsProject[]) =
                let mutable r = false
                let mutable s = ""
                x.GetMkDocument(VSConstants.VSITEMID_ROOT, &s) |> ignore
                let ourMoniker = s
                for i = 0 to cProjects-1 do
                    rgpProjects.[i].GetMkDocument(VSConstants.VSITEMID_ROOT, &s) |> ignore
                    if s = ourMoniker then
                        r <- true
                r
                    
            interface Microsoft.Build.Framework.ITaskHost                
                // no members

            interface IVsTrackProjectDocumentsEvents2 with
                member x.OnAfterAddFilesEx(cProjects, _cFiles, rgpProjects, _rgFirstIndices, rgpszMkDocuments, _rgFlags) = 
                    if x.OneOfTheProjectsIsThisOne(cProjects, rgpProjects) then
                        match addFilesNotification with
                        | Some(f) -> f rgpszMkDocuments
                        | None -> ()
                        x.ComputeSourcesAndFlags()
                    VSConstants.S_OK
                member x.OnAfterAddDirectoriesEx(_cProjects,_cDirectories, _rgpProjects,_rgFirstIndices,_rgpszMkDocuments,  _rgFlags) = 
                    VSConstants.S_OK
                member x.OnAfterRemoveFiles(cProjects,_cFiles, rgpProjects,_rgFirstIndices,_rgpszMkDocuments,  _rgFlags) = 
                    if x.OneOfTheProjectsIsThisOne(cProjects, rgpProjects) then
                        x.ComputeSourcesAndFlags()
                    VSConstants.S_OK
                member x.OnAfterRemoveDirectories(_cProjects,_cDirectories, _rgpProjects,_rgFirstIndices,_rgpszMkDocuments,  _rgFlags) = 
                    VSConstants.S_OK
                member x.OnAfterRenameFiles(cProjects,_cFiles, rgpProjects,_rgFirstIndices,_rgszMkOldNames,_rgszMkNewNames,  _rgFlags) = 
                    if x.OneOfTheProjectsIsThisOne(cProjects, rgpProjects) then
                        x.ComputeSourcesAndFlags()
                    VSConstants.S_OK
                member x.OnAfterRenameDirectories(_cProjects,_cDirs, _rgpProjects,_rgFirstIndices,_rgszMkOldNames,_rgszMkNewNames,  _rgFlags) = 
                    VSConstants.S_OK
                member x.OnAfterSccStatusChanged(_cProjects,_cFiles, _rgpProjects,_rgFirstIndices,_rgpszMkDocuments,  _rgdwSccStatus) = 
                    VSConstants.S_OK
                member x.OnQueryAddFiles(_pProject,_cFiles,_rgpszMkDocuments,   _rgFlags,   _pSummaryResult,   _rgResults) = 
                    VSConstants.S_OK
                member x.OnQueryRenameFiles(_pProject,_cFiles,_rgszMkOldNames,_rgszMkNewNames,   _rgFlags,   _pSummaryResult,   _rgResults) = 
                    VSConstants.S_OK
                member x.OnQueryRenameDirectories(_pProject,_cDirs,_rgszMkOldNames,_rgszMkNewNames,   _rgFlags,   _pSummaryResult,   _rgResults) = 
                    VSConstants.S_OK
                member x.OnQueryAddDirectories(_pProject,_cDirectories,_rgpszMkDocuments,   _rgFlags,   _pSummaryResult,   _rgResults) = 
                    VSConstants.S_OK
                member x.OnQueryRemoveFiles(_pProject,_cFiles,_rgpszMkDocuments,   _rgFlags,   _pSummaryResult,   _rgResults) = 
                    VSConstants.S_OK
                member x.OnQueryRemoveDirectories(_pProject,_cDirectories,_rgpszMkDocuments,   _rgFlags,   _pSummaryResult,   _rgResults) = 
                    VSConstants.S_OK

            // Without this, fsc.exe compiles to DLL but assembly is bad and can't be loaded.
            // (fsc.exe does give a warning when this is missing)
            interface IVsProjectSpecificEditorMap with 
                member x.GetSpecificEditorType( _mkDocument:string, guidEditorType:byref<Guid> ) =
                    // Ideally we should at this point initalize a File extension to EditorFactory guid Map e.g.
                    // in the registry hive so that more editors can be added without changing this part of the
                    // code. FSharp only makes usage of one Editor Factory and therefore we will return 
                    // that guid
                    guidEditorType <- new Guid(FSharpCommonConstants.editorFactoryGuidString)
                    VSConstants.S_OK

            interface IVsProjectSpecificEditorMap2 with 
                member x.GetSpecificEditorProperty(_mkDocument:string, _propid:int, result: byref<obj>) =
                    // initialize output params
                    result <- null
                    VSConstants.E_NOTIMPL

                member x.GetSpecificEditorType( _mkDocument:string, guidEditorType:byref<Guid> ) =
                    // Ideally we should at this point initalize a File extension to EditorFactory guid Map e.g.
                    // in the registry hive so that more editors can be added without changing this part of the
                    // code. FSharp only makes usage of one Editor Factory and therefore we will return 
                    // that guid
                    guidEditorType <- new Guid(FSharpCommonConstants.editorFactoryGuidString)
                    VSConstants.S_OK

                member x.GetSpecificLanguageService(_mkDocument:string, guidLanguageService:byref<Guid> ) =
                    guidLanguageService <- Guid.Empty
                    VSConstants.E_NOTIMPL

                member x.SetSpecificEditorProperty(_mkDocument:string, _propid:int, _value:obj ) =
                    VSConstants.E_NOTIMPL
            end
        
    type internal ActiveCfgBatchUpdateState =
        | NonBatch
        | BatchWaiting
        | BatchDone

    // Why is this a separate class, rather than an interface implemented on
    // FSharpProjectNode?  Because, at the time of initial registration of this
    // interface, we are still initializing FSharpProjectNode itself, and trying
    // to cast "this" (FSharpProjectNode) to an IVsFoo and passing it to VS wraps
    // the object in a COM CCW wrapper, which is then unexpected when the startup
    // code later comes along and tries to CCW wrap it again.  Using a separate 
    // class means we have a separate object to CCW wrap, avoiding the problematic
    // "double CCW-wrapping" of the same object.
    type internal SolutionEventsListener(projNode) =

            let mutable waitDialog : IDisposable option = None

            // During batch active project configuration changes, make sure we only run CSAF once
            // per batch. Before this change, OnActiveProjectCfgChange was being called twice per
            // batch per project.
            let mutable batchState = NonBatch
            
            // The CCW wrapper seems to prevent an object-identity test, so we determine whether
            // two IVsHierarchy objects are equal by comparing their captions.  (It's ok if this
            // occasionally yields false positives, as this just means we may do a little extra
            // background work.)
            let GetCaption(hier : IVsHierarchy) =
                if hier = null then
                    null : System.String
                else
                    let mutable o : obj = null
                    let r = hier.GetProperty(VSConstants.VSITEMID_ROOT, int(__VSHPROPID.VSHPROPID_Caption), &o)
                    if r = VSConstants.S_OK then
                        o :?> System.String
                    else
                        null : System.String

            let OnActiveProjectCfgChange(pIVsHierarchy) =
                if GetCaption(pIVsHierarchy) = GetCaption(projNode.InteropSafeIVsHierarchy) && batchState <> BatchDone then
                    projNode.SetProjectFileDirty(projNode.IsProjectFileDirty)
                    projNode.ComputeSourcesAndFlags()
                    
                    if batchState = BatchWaiting then
                        batchState <- BatchDone
                VSConstants.S_OK

            let UpdateConfig(pHierProj) =
                // By default, the F# project system keeps its own internal Configuration and Platform in sync with the current active
                // Configuration and Platform by listening for OnActiveProjectCfgChange events.  However there is one case where the
                // active cfg changes without an event, and this is during 'Batch Build'.  So we listen for the start and end of 
                // Batch Build, and manually update the project to the active cfg before/after to set/reset the config.
                if GetCaption(pHierProj) = GetCaption(projNode.InteropSafeIVsHierarchy) then
                    // This code matches what ProjectNode.SetConfiguration would do; that method cannot be called during a build, but at this
                    // current moment in time, it is 'safe' to do this update.
                    let _,currentConfigName = Utilities.TryGetActiveConfigurationAndPlatform(projNode.Site, projNode.ProjectIDGuid)
                    MSBuildProject.SetGlobalProperty(projNode.BuildProject, ProjectFileConstants.Configuration, currentConfigName.ConfigName)
                    MSBuildProject.SetGlobalProperty(projNode.BuildProject, ProjectFileConstants.Platform, currentConfigName.MSBuildPlatform)
                    projNode.UpdateMSBuildState()

            interface IVsUpdateSolutionEvents with
                member x.UpdateSolution_Begin(pfCancelUpdate) =
                    VSConstants.S_OK
                member x.UpdateSolution_Done(_fSucceeded, _fModified, _fCancelCommand) =
                    VSConstants.S_OK
                member x.UpdateSolution_StartUpdate(pfCancelUpdate) =
                    VSConstants.S_OK
                member x.UpdateSolution_Cancel() =
                    VSConstants.S_OK
                member x.OnActiveProjectCfgChange(pIVsHierarchy) =
                    OnActiveProjectCfgChange(pIVsHierarchy)

            interface IVsUpdateSolutionEvents2 with
                member x.UpdateSolution_Begin(pfCancelUpdate) =
                    VSConstants.S_OK
                member x.UpdateSolution_Done(_fSucceeded, _fModified, _fCancelCommand) =
                    VSConstants.S_OK
                member x.UpdateSolution_StartUpdate(pfCancelUpdate) =
                    VSConstants.S_OK
                member x.UpdateSolution_Cancel() =
                    VSConstants.S_OK
                member x.OnActiveProjectCfgChange(pIVsHierarchy) =
                    OnActiveProjectCfgChange(pIVsHierarchy)
                member x.UpdateProjectCfg_Begin(pHierProj, _pCfgProj, _pCfgSln, _dwAction, pfCancel) =
                    UpdateConfig(pHierProj)
                    VSConstants.S_OK
                member x.UpdateProjectCfg_Done(pHierProj, _pCfgProj, _pCfgSln, _dwAction, _fSuccess, _fCancel) =
                    UpdateConfig(pHierProj)
                    VSConstants.S_OK

            interface IVsUpdateSolutionEvents3 with
                member x.OnBeforeActiveSolutionCfgChange(_oldCfg, _newCfg) =
                    // this will be called for each project, but wait dialogs cannot 'stack'
                    // i.e. if a wait dialog is already open, subsequent calls to StartWaitDialog
                    // will not override the current open dialog
                    waitDialog <-
                        {
                            WaitCaption = FSharpSR.GetString FSharpSR.ProductName
                            WaitMessage = FSharpSR.GetString FSharpSR.UpdatingSolutionConfiguration
                            ProgressText = None
                            StatusBmpAnim = null
                            StatusBarText = None
                            DelayToShowDialogSecs = 1
                            IsCancelable = false
                            ShowMarqueeProgress = true
                        }
                        |> WaitDialog.start projNode.Site
                        |> Some

                    VSConstants.S_OK

                member x.OnAfterActiveSolutionCfgChange(_oldCfg, _newCfg) =
                    match waitDialog with
                    | Some x ->
                        x.Dispose()
                        waitDialog <- None
                    | None -> ()
                    VSConstants.S_OK
              
            interface IVsUpdateSolutionEvents4 with
                member x.OnActiveProjectCfgChangeBatchBegin() =
                    batchState <- BatchWaiting
                member x.OnActiveProjectCfgChangeBatchEnd() =
                    batchState <- NonBatch
                member x.UpdateSolution_BeginFirstUpdateAction() =
                    ()
                member x.UpdateSolution_BeginUpdateAction(_dwAction) =
                    ()
                member x.UpdateSolution_EndLastUpdateAction() =
                    ()
                member x.UpdateSolution_EndUpdateAction(_dwAction) =
                    ()
                member x.UpdateSolution_QueryDelayFirstUpdateAction(_pfDelay) =
                    ()

            interface IVsTrackProjectRetargetingEvents with
                override this.OnRetargetingBeforeChange
                    ([<In ; MarshalAs(UnmanagedType.LPWStr)>] _projRef : string,
                     _pBeforeChangeHier : IVsHierarchy,
                     [<In ; MarshalAs(UnmanagedType.LPWStr)>] _currentTargetFramework : string,
                     [<In ; MarshalAs(UnmanagedType.LPWStr)>] _newTargetFramework : string,
                     pCanceled : byref<bool>,
                     [<Out ; MarshalAs(UnmanagedType.LPWStr)>] ppReasonMsg : byref<string>) =
                     ppReasonMsg <- null
                     0

                override this.OnRetargetingCanceledChange
                    ([<In ; MarshalAs(UnmanagedType.LPWStr)>] _projRef : string,
                     _pBeforeChangeHier : IVsHierarchy,
                     [<In ; MarshalAs(UnmanagedType.LPWStr)>] _currentTargetFramework : string,
                     [<In ; MarshalAs(UnmanagedType.LPWStr)>] _newTargetFramework : string) =
                     0

                override this.OnRetargetingBeforeProjectSave
                    ([<In ; MarshalAs(UnmanagedType.LPWStr)>] _projRef : string,
                     _pBeforeChangeHier : IVsHierarchy,
                     [<In ; MarshalAs(UnmanagedType.LPWStr)>] _currentTargetFramework : string,
                     [<In ; MarshalAs(UnmanagedType.LPWStr)>] _newTargetFramework : string) =
                     0

                override this.OnRetargetingAfterChange
                    ([<In ; MarshalAs(UnmanagedType.LPWStr)>] _projRef : string,
                     _pAfterChangeHier : IVsHierarchy,
                     [<In ; MarshalAs(UnmanagedType.LPWStr)>] _fromTargetFramework : string,
                     [<In ; MarshalAs(UnmanagedType.LPWStr)>] _toTargetFramework : string) =
                     0

                override this.OnRetargetingFailure
                    ([<In ; MarshalAs(UnmanagedType.LPWStr)>] _projRef : string,
                     _pHier : IVsHierarchy,
                     [<In ; MarshalAs(UnmanagedType.LPWStr)>] _fromTargetFramework : string,
                     [<In ; MarshalAs(UnmanagedType.LPWStr)>] _toTargetFramework : string) =
                     0
                

    [<ComVisible(true)>] 
    [<CLSCompliant(false)>]
    [<System.Runtime.InteropServices.ClassInterface(ClassInterfaceType.AutoDual)>]
    [<Guid("0337B405-3FEF-455C-A725-AA188C38F217")>]
    type public FSharpProjectNodeProperties internal (node:FSharpProjectNode) = 
        inherit ProjectNodeProperties(node)         

        [<Browsable(false)>]
        member this.CanUseTargetFSharpCoreVersion = node.CanUseTargetFSharpCoreReference

        [<Browsable(false)>]
        member this.TargetFSharpCoreVersion 
            with get() : string = node.TargetFSharpCoreVersion
             and set(v) = node.TargetFSharpCoreVersion <- v

        [<Browsable(false)>]
        member this.DefaultNamespace 
            with get() : string = 
                let hier = node.InteropSafeIVsHierarchy
                let mutable o : obj = null
                let hr = hier.GetProperty(VSConstants.VSITEMID_ROOT, int32 __VSHPROPID.VSHPROPID_DefaultNamespace, &o)
                if hr = VSConstants.S_OK then
                    o :?> System.String
                else
                    null : System.String
            and set(value : string) = 
                let hier = node.InteropSafeIVsHierarchy
                hier.SetProperty(VSConstants.VSITEMID_ROOT, int32 __VSHPROPID.VSHPROPID_DefaultNamespace, value) |> ignore
               

        [<Browsable(false)>]
        member this.TargetFramework 
            with get() : uint32 =                
                let moniker = this.TargetFrameworkMoniker
                let frameworkName = new System.Runtime.Versioning.FrameworkName(moniker)
                let ver = frameworkName.Version
                (uint32 ver.Major) <<< 16 ||| (uint32 ver.Minor)
                
            and set(value : uint32) =                          
                let version = new Version(int(value >>> 16), int(value &&& 0xFFFFu))
                let currentMoniker = this.Node.GetTargetFrameworkMoniker()
                let currentFrameworkName = new System.Runtime.Versioning.FrameworkName(currentMoniker)
                let newMoniker = new System.Runtime.Versioning.FrameworkName(currentFrameworkName.Identifier, version, currentFrameworkName.Profile)                                                
                let fullName = // TODO: 5571 tarcks replacing this with newMoniker.FullName
                    let s = sprintf "%s,Version=v%s" currentFrameworkName.Identifier (version.ToString(2))
                    if String.IsNullOrEmpty(newMoniker.Profile) then s
                    else s + (sprintf ",Profile=%s" newMoniker.Profile)                
                this.TargetFrameworkMoniker <- fullName


        [<Browsable(false)>]
        member this.TargetFrameworkMoniker
            with get() : string =
                let hier = node.InteropSafeIVsHierarchy
                let mutable o : obj = null
                let hr = hier.GetProperty(VSConstants.VSITEMID_ROOT, int32 __VSHPROPID4.VSHPROPID_TargetFrameworkMoniker, &o)
                if hr = VSConstants.S_OK then
                    o :?> System.String
                else
                    null : System.String

            and set(value : string) =
                let oldValue = this.TargetFrameworkMoniker
                if not (String.Equals(oldValue, value, StringComparison.OrdinalIgnoreCase)) then
                    if not (Utilities.IsInAutomationFunction(node.Site)) then
                        let newFrameworkName = System.Runtime.Versioning.FrameworkName(value)
                        // Silverlight projects in Dev11 support only Silverlight 5
                        if newFrameworkName.Identifier = "Silverlight" && newFrameworkName.Version.Major <> 5 then 
                            VsShellUtilities.ShowMessageBox
                                (
                                    node.Site, 
                                    FSharpSR.GetString(FSharpSR.Dev11SupportsOnlySilverlight5), 
                                    null,
                                    OLEMSGICON.OLEMSGICON_INFO, OLEMSGBUTTON.OLEMSGBUTTON_OK, OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST
                                ) |> ignore
                            Marshal.ThrowExceptionForHR(VSConstants.OLE_E_PROMPTSAVECANCELLED)
                        let result =
                            VsShellUtilities.ShowMessageBox(node.Site, FSharpSR.GetStringWithCR(FSharpSR.NeedReloadToChangeTargetFx), 
                                                        null,
                                                        OLEMSGICON.OLEMSGICON_QUERY, OLEMSGBUTTON.OLEMSGBUTTON_YESNO, OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST) 
                        if result <> NativeMethods.IDYES then
                            Marshal.ThrowExceptionForHR(VSConstants.OLE_E_PROMPTSAVECANCELLED)

                    let hier = node.InteropSafeIVsHierarchy
                    hier.SetProperty(VSConstants.VSITEMID_ROOT, int32 __VSHPROPID4.VSHPROPID_TargetFrameworkMoniker, value) |> ignore

        [<Browsable(false)>]
        member this.OutputFileName = ((this.Node.ProjectMgr :?> FSharpProjectNode).OutputFileName)
        
        // Application property page properties
        [<Browsable(false)>]
        member this.AssemblyName 
            with get() = this.Node.ProjectMgr.GetProjectProperty(ProjectFileConstants.AssemblyName)
            and set(value) = this.Node.ProjectMgr.SetProjectProperty(ProjectFileConstants.AssemblyName, value)

        [<Browsable(false)>]
        member this.RootNamespace 
            with get() = this.Node.ProjectMgr.GetProjectProperty(ProjectFileConstants.RootNamespace)
            and set(value) = this.Node.ProjectMgr.SetProjectProperty(ProjectFileConstants.RootNamespace, value)

        [<Browsable(false)>]
        member this.Win32ResourceFile
            with get() = this.Node.ProjectMgr.GetProjectProperty(ProjectFileConstants.Win32Resource)
            and set(value) = this.Node.ProjectMgr.SetProjectProperty(ProjectFileConstants.Win32Resource, value)
                
        [<Browsable(false)>]
        member this.OutputType
            with get() = 
                let outputTypeString = this.Node.ProjectMgr.GetProjectProperty(ProjectFileConstants.OutputType)
                match outputTypeString with
                | "WinExe"  -> OutputType.WinExe
                | "Library" -> OutputType.Library
                | _         -> OutputType.Exe
            and set(value) = 
                let outputTypeInteger = 
                    match value with
                    | OutputType.WinExe -> "WinExe"
                    | OutputType.Exe -> "Exe"
                    | OutputType.Library -> "Library"
                    | _ -> raise <| ArgumentException(FSharpSR.GetString(FSharpSR.InvalidOutputType), "value")
                this.Node.ProjectMgr.SetProjectProperty(ProjectFileConstants.OutputType, outputTypeInteger)
        
        // Build Events Page Properties
        [<Browsable(false)>]
        member this.PreBuildEvent
            with get() = this.Node.ProjectMgr.GetUnevaluatedProjectProperty(ProjectFileConstants.PreBuildEvent, true)
            and set(value) = this.Node.ProjectMgr.SetOrCreateBuildEventProperty(ProjectFileConstants.PreBuildEvent, value)
        
        [<Browsable(false)>]
        member this.PostBuildEvent
            with get() = this.Node.ProjectMgr.GetUnevaluatedProjectProperty(ProjectFileConstants.PostBuildEvent, true)
            and set(value) = this.Node.ProjectMgr.SetOrCreateBuildEventProperty(ProjectFileConstants.PostBuildEvent, value)
        
        [<Browsable(false)>]
        member this.RunPostBuildEvent
            with get() = 
                let runPostBuildEventString = this.Node.ProjectMgr.GetProjectProperty(ProjectFileConstants.RunPostBuildEvent)
                match runPostBuildEventString with
                | "OnBuildSuccess"  -> 1
                | "OnOutputUpdated" -> 2
                | _                 -> 0  // "Always"
            and set(value) = 
                let runPostBuildEventInteger = 
                    match value with
                    | 0 -> "Always"
                    | 1 -> "OnBuildSuccess"
                    | 2 -> "OnOutputUpdated"
                    | _ -> raise <| ArgumentException(FSharpSR.GetString(FSharpSR.InvalidRunPostBuildEvent), "value")
                this.Node.ProjectMgr.SetProjectProperty(ProjectFileConstants.RunPostBuildEvent, runPostBuildEventInteger)
        
    type internal FSharpFolderNode(root : FSharpProjectNode, relativePath : string, projectElement : ProjectElement) =
            inherit FolderNode(root, relativePath, projectElement)

            override x.QueryStatusOnNode(guidCmdGroup:Guid, cmd:uint32, pCmdText:IntPtr, result:byref<QueryStatusResult>) =
                if (guidCmdGroup = VSProjectConstants.guidFSharpProjectCmdSet) &&
                   (cmd = (uint32)VSProjectConstants.MoveUpCmd.ID) then
                        result <- result ||| QueryStatusResult.SUPPORTED
                        if FSharpFileNode.CanMoveUp(x) then
                            result <- result ||| QueryStatusResult.ENABLED
                        VSConstants.S_OK
                elif (guidCmdGroup = VSProjectConstants.guidFSharpProjectCmdSet) &&
                   (cmd = (uint32)VSProjectConstants.MoveDownCmd.ID) then
                        result <- result ||| QueryStatusResult.SUPPORTED
                        if FSharpFileNode.CanMoveDown(x) then
                            result <- result ||| QueryStatusResult.ENABLED
                        VSConstants.S_OK
                else
                        base.QueryStatusOnNode(guidCmdGroup, cmd, pCmdText, &result)

            override x.ExecCommandOnNode(guidCmdGroup:Guid, cmd:uint32, nCmdexecopt:uint32, pvaIn:IntPtr, pvaOut:IntPtr ) =
                if (guidCmdGroup = VSProjectConstants.guidFSharpProjectCmdSet) &&
                   (cmd = (uint32)VSProjectConstants.MoveUpCmd.ID) then 
                       FSharpFileNode.MoveUp(x, root)
                       VSConstants.S_OK
                elif (guidCmdGroup = VSProjectConstants.guidFSharpProjectCmdSet) &&
                   (cmd = (uint32)VSProjectConstants.MoveDownCmd.ID) then 
                       FSharpFileNode.MoveDown(x, root)
                       VSConstants.S_OK
                else
                        base.ExecCommandOnNode(guidCmdGroup, cmd, nCmdexecopt, pvaIn, pvaOut)
            
    type internal FSharpBuildAction =
       | None = 0
       | Compile = 1
       | Content = 2
       | EmbeddedResource = 3
       | ApplicationDefinition = 4
       | Page = 5
       | Resource  = 6
       
    type public FSharpBuildActionPropertyDescriptor internal (prop : PropertyDescriptor) =
        inherit PropertyDescriptor(prop)

        override this.DisplayName = SR.BuildAction

        override this.ComponentType = typeof<FSharpFileNodeProperties>

        override this.PropertyType = typeof<VSLangProj.prjBuildAction>

        override this.IsReadOnly = false

        override this.GetEditor(editorBaseType : Type) = this.CreateInstance(editorBaseType)

        override this.Converter = null

        override this.CanResetValue(o : obj) = prop.CanResetValue(o)

        override this.GetValue (o : obj) =
            prop.GetValue(o)

        override this.SetValue (o : obj, value : obj) =
            prop.SetValue(o, value)

        override this.ResetValue (o : obj) = prop.ResetValue(o)

        override this.ShouldSerializeValue(o : obj) = prop.ShouldSerializeValue(o)


    [<ComVisible(true)>] 
    [<CLSCompliant(false)>]
    [<Guid("9D8E1EFB-1F18-4E2F-8C67-77328A274718")>]
    type public FSharpFileNodeProperties internal (node:HierarchyNode) = 
        inherit FileNodeProperties(node)

        [<Browsable(false)>]
        member x.Url = "file:///" + x.Node.Url

        [<Browsable(false)>]
        member x.SubType 
            with get() = (x.Node :?> FSharpFileNode).SubType
            and set(value) = (x.Node :?> FSharpFileNode).SubType <- value

        override x.CreateDesignPropertyDescriptor propertyDescriptor =
            let isLinkFile = 
                match x.Node with
                | :? FSharpFileNode as f -> f.IsLinkFile
                | _ -> false

            let fileNameEditable = not isLinkFile

            if (not(fileNameEditable) && (propertyDescriptor.Name = "FileName"))
            then Microsoft.VisualStudio.Editors.PropertyPages.FilteredObjectWrapper.ReadOnlyPropertyDescriptorWrapper(propertyDescriptor) :> PropertyDescriptor
            else base.CreateDesignPropertyDescriptor(propertyDescriptor)


    /// Represents most (non-reference) nodes in the solution hierarchy of an F# project (e.g. foo.fs, bar.fsi, app.config)
    type internal FSharpFileNode(root:FSharpProjectNode, e:ProjectElement, hierarchyId) = 
            inherit LinkedFileNode(root,e, hierarchyId)

            static let protectVisualState (root : FSharpProjectNode) (node : HierarchyNode) f = 
                let uiWin = UIHierarchyUtilities.GetUIHierarchyWindow(root.Site, HierarchyNode.SolutionExplorer)
                let expanded = 
                    let mutable result = 0u
                    uiWin.GetItemState(root.InteropSafeIVsUIHierarchy, node.ID, uint32 __VSHIERARCHYITEMSTATE.HIS_Expanded, &result) |> ignore
                    (result &&& (uint32 __VSHIERARCHYITEMSTATE.HIS_Expanded)) <> 0u

                let r = f ()

                uiWin.ExpandItem(root.InteropSafeIVsUIHierarchy, node.ID, EXPANDFLAGS.EXPF_SelectItem)
                |> Microsoft.VisualStudio.ErrorHandler.ThrowOnFailure
                |> ignore
                let flag = if expanded then EXPANDFLAGS.EXPF_ExpandFolder else EXPANDFLAGS.EXPF_CollapseFolder
                uiWin.ExpandItem(root.InteropSafeIVsUIHierarchy, node.ID, flag)
                |> Microsoft.VisualStudio.ErrorHandler.ThrowOnFailure 
                |> ignore
                r

            let mutable selectionChangedListener : SelectionElementValueChangedListener option = 
                let iOle = match root.GetService(typeof<IOleServiceProvider>) with
                           | :? IOleServiceProvider as x -> x
                           | _ -> null
                let sp = new Microsoft.VisualStudio.Shell.ServiceProvider(iOle)

                Some(new SelectionElementValueChangedListener(sp))

            do selectionChangedListener.Value.Init()
                        
            override x.IsNonMemberItem with get() = false

            override x.RenameFileNode(oldname, newname, parentId) =
                // The base class will move to bottom of solution explorer after renaming, so we need to move it back.
                // remember where we are now
                let mutable relative : HierarchyNode = null  // a file above or below us we can use as a benchmark/anchor to get back to same order/place
                let mutable iBelongBeforeRelative = true     // do we go before or after it?
                if not (obj.ReferenceEquals(x.NextSibling, null)) then
                    relative <- x.NextSibling
                    iBelongBeforeRelative <- true
                else
                    let mutable tmp = x.Parent.FirstChild
                    if not (obj.ReferenceEquals(tmp, x)) then
                        while not (obj.ReferenceEquals(tmp.NextSibling, x)) do
                            tmp <- tmp.NextSibling
                        relative <- tmp
                        iBelongBeforeRelative <- false
                // rename
                let mutable ok = false
                try
                    let result = base.RenameFileNode(oldname, newname, parentId)
                    ok <- true
                    result
                finally
                    // move back if necessary
                    // note that if relative = null, then we were the only file and no movement correction is necessary
                    if not (obj.ReferenceEquals(relative, null)) then
                        // also, RenameFileNode might throw, either before or after actually doing a delete&add
                        let threwAfterAddAndDelete = (not(ok) && x.Parent = null)
                        if ok || threwAfterAddAndDelete then
                            let uiWin = UIHierarchyUtilities.GetUIHierarchyWindow(root.Site, HierarchyNode.SolutionExplorer)
                            let movedNode =
                                if iBelongBeforeRelative then
                                    FSharpFileNode.MoveLastToAbove(relative, root)
                                else
                                    FSharpFileNode.MoveLastToBelow(relative, root)
                            let hr = uiWin.ExpandItem(root.InteropSafeIVsUIHierarchy, movedNode.ID, EXPANDFLAGS.EXPF_SelectItem) // keep highlighting selection on the just-renamed node
                            Microsoft.VisualStudio.ErrorHandler.ThrowOnFailure(hr) |> ignore

            /// Returns bool indicating whether this node is of subtype "Form"
            member x.IsFormSubType =
                let result = x.ItemNode.GetMetadata(ProjectFileConstants.SubType)
                not (String.IsNullOrEmpty(result)) && (String.Compare(result, ProjectFileAttributeValue.Form, true, CultureInfo.InvariantCulture) = 0) 

            /// Returns the SubType of an FSharp FileNode. It is 

            member  x.SubType 
                with get() = x.ItemNode.GetMetadata(ProjectFileConstants.SubType)
                and set(value) = x.ItemNode.SetMetadata(ProjectFileConstants.SubType, value)

            override x.CreatePropertiesObject() =
                let properties = new FSharpFileNodeProperties(x)
                (properties :> NodeProperties)
           
            member x.DisposeSelectionListener() = 
                begin match selectionChangedListener with 
                | None -> ()
                | Some listener -> 
                   listener.Dispose()
                   selectionChangedListener <- None
                end

            override x.Close() =
                x.DisposeSelectionListener()
                base.Close()
                
            override x.Dispose(disposing) =
                try 
                    x.DisposeSelectionListener()
                finally
                    base.Dispose(disposing)                

            override x.ImageIndex =
                    if (x.IsFormSubType) then 
                        int32 ProjectNode.ImageName.WindowsForm
                    elif (FSharpProjectNode.IsFSharpCodeFileIconwise(x.FileName)) then 
                        FSharpProjectNode.ImageOffset + int32 FSharpImageName.FsFile
                    elif (FSharpProjectNode.IsFSharpSignatureFileIconwise(x.FileName)) then 
                        FSharpProjectNode.ImageOffset + int32 FSharpImageName.FsiFile
                    elif (FSharpProjectNode.IsFSharpScriptFileIconwise(x.FileName)) then 
                        FSharpProjectNode.ImageOffset + int32 FSharpImageName.FsxFile
                    else
                        base.ImageIndex

            /// Open a file depending on the SubType property associated with the file item in the project file
            override x.DoDefaultAction() =
                let manager = (x.GetDocumentManager() :?> FileDocumentManager)
                Debug.Assert(manager <> null, "Could not get the FileDocumentManager")

                let viewGuid = (if x.IsFormSubType then VSConstants.LOGVIEWID_Designer else VSConstants.LOGVIEWID_Primary)
                let fallbackViewGuid = (if x.IsFormSubType then VSConstants.LOGVIEWID_Primary else VSConstants.LOGVIEWID_Designer)
                let mutable frame : IVsWindowFrame = null
                manager.Open(false, false, viewGuid, fallbackViewGuid, &frame, WindowFrameShowAction.Show) |> ignore

            /// In solution explorer, move the last of my siblings to just above me, return the moved FSharpFileNode
            static member MoveLastToAbove(target : HierarchyNode, root : FSharpProjectNode) : FSharpFileNode =
                let thisNode = target
                let mutable lastNode = target.NextSibling
                let mutable nextToLastNode = target
                while lastNode.NextSibling <> null do
                    lastNode <- lastNode.NextSibling 
                    nextToLastNode <- nextToLastNode.NextSibling 
                
                // unlink from end
                nextToLastNode.NextSibling <- null
                lastNode.OnItemDeleted()
                // link before me
                if obj.ReferenceEquals(thisNode.Parent.FirstChild, thisNode) then
                    thisNode.Parent.FirstChild <- lastNode
                    lastNode.NextSibling <- thisNode
                    
                else
                    let mutable nodeBeforeMe = thisNode.Parent.FirstChild
                    while not( obj.ReferenceEquals(nodeBeforeMe.NextSibling, thisNode) ) do
                        nodeBeforeMe <- nodeBeforeMe.NextSibling 
                    nodeBeforeMe.NextSibling <- lastNode
                    lastNode.NextSibling <- thisNode
                
                root.OnItemAdded(lastNode.Parent, lastNode)
                lastNode :?> FSharpFileNode

            /// In solution explorer, move the last of my siblings to just below me, return the moved FSharpFileNode
            static member MoveLastToBelow(target : HierarchyNode, root : FSharpProjectNode) : FSharpFileNode =
                let mutable lastNode = target.NextSibling 
                let mutable nextToLastNode = target
                while lastNode.NextSibling <> null do
                    lastNode <- lastNode.NextSibling 
                    nextToLastNode <- nextToLastNode.NextSibling 
                
                // unlink from end
                nextToLastNode.NextSibling <- null
                lastNode.OnItemDeleted()

                // link into middle
                let tmp = target.NextSibling 
                target.NextSibling <- lastNode
                lastNode.NextSibling <- tmp
                root.OnItemAdded(lastNode.Parent, lastNode)
                lastNode :?> FSharpFileNode

            override x.ExecCommandOnNode(guidCmdGroup:Guid, cmd:uint32, nCmdexecopt:uint32, pvaIn:IntPtr, pvaOut:IntPtr ) =
                Debug.Assert(x.ProjectMgr <> null, "The FSharpFileNode has no project manager")

                let completeRenameIfNecessary() = 
                    let tree = 
                        match UIHierarchyUtilities.GetUIHierarchyWindow(root.Site, HierarchyNode.SolutionExplorer) with
                        | :? Microsoft.VisualStudio.PlatformUI.SolutionNavigatorPane as snp ->
                            match snp.Navigator with 
                            | null -> null 
                            | n -> 
                                match n.TreeView with 
                                | null -> null
                                | t -> t
                        | _ -> null
                    if tree <> null && tree.IsInRenameMode then
                        let id = x.ID
                        let oldName = x.GetEditLabel()
                        // if tree is in rename mode now - commit renaming
                        // since rename is implemented via remove\add set of operations - after renaming we need to fetch node that corresponds to the current one

                        // rename may fail (i.e if new name contains invalid characters), in this case user will see error message and after that failure will be swallowed
                        // if this happens - we need to cancel current transaction,
                        // otherwise it will hold current hierarchy node. After move operation is completed - current node will become invalid => may lead to ObjectDisposedExceptions.
                        // Since error is not appear directly in the code - we check if old and new labels match and if yes - treat it as reason that error happens
                        tree.CommitRename(Microsoft.Internal.VisualStudio.PlatformUI.RenameItemCompletionFocusBehavior.Refocus)
                        
                        let node = root.ItemIdMap.[id] :?> FSharpFileNode
                        if node.GetEditLabel() = oldName then
                            tree.CancelRename(Microsoft.Internal.VisualStudio.PlatformUI.RenameItemCompletionFocusBehavior.Refocus)
                        node
                    else
                        x
                
                if (x.ProjectMgr= null) then 
                    raise <| InvalidOperationException()

                if (guidCmdGroup = VSProjectConstants.guidFSharpProjectCmdSet) &&
                   (cmd = (uint32)VSProjectConstants.MoveUpCmd.ID) then 
                       let actualNode = completeRenameIfNecessary()
                       FSharpFileNode.MoveUp(actualNode, root)
                       root.EnsureMSBuildAndSolutionExplorerAreInSync()
                       VSConstants.S_OK

                elif (guidCmdGroup = VSProjectConstants.guidFSharpProjectCmdSet) &&
                   (cmd = (uint32)VSProjectConstants.MoveDownCmd.ID) then 
                       let actualNode = completeRenameIfNecessary()
                       FSharpFileNode.MoveDown(actualNode, root)
                       root.EnsureMSBuildAndSolutionExplorerAreInSync()
                       VSConstants.S_OK

                elif (guidCmdGroup = VSProjectConstants.guidFSharpProjectCmdSet) &&
                   (cmd = (uint32)VSProjectConstants.AddNewItemAbove.ID) then 
                        let result = root.MoveNewlyAddedFileAbove (x, fun () ->
                            root.AddItemToHierarchy(HierarchyAddType.AddNewItem))
                        root.EnsureMSBuildAndSolutionExplorerAreInSync()
                        result
                        
                elif (guidCmdGroup = VSProjectConstants.guidFSharpProjectCmdSet) &&
                   (cmd = (uint32)VSProjectConstants.AddExistingItemAbove.ID) then 
                        let result = root.MoveNewlyAddedFileAbove (x, fun () ->
                            root.AddItemToHierarchy(HierarchyAddType.AddExistingItem))
                        root.EnsureMSBuildAndSolutionExplorerAreInSync()
                        result
                        
                elif (guidCmdGroup = VSProjectConstants.guidFSharpProjectCmdSet) &&
                   (cmd = (uint32)VSProjectConstants.AddNewItemBelow.ID) then 
                        let result = root.MoveNewlyAddedFileBelow (x, fun () ->
                            root.AddItemToHierarchy(HierarchyAddType.AddNewItem))
                        root.EnsureMSBuildAndSolutionExplorerAreInSync()
                        result
                        
                elif (guidCmdGroup = VSProjectConstants.guidFSharpProjectCmdSet) &&
                   (cmd = (uint32)VSProjectConstants.AddExistingItemBelow.ID) then 
                        let result = root.MoveNewlyAddedFileBelow (x, fun () ->
                            root.AddItemToHierarchy(HierarchyAddType.AddExistingItem))
                        root.EnsureMSBuildAndSolutionExplorerAreInSync()
                        result
                        
                else
                    base.ExecCommandOnNode(guidCmdGroup, cmd, nCmdexecopt, pvaIn, pvaOut)
 
            /// Handles the menuitems
            override x.QueryStatusOnNode(guidCmdGroup:Guid, cmd:uint32, pCmdText:IntPtr, result:byref<QueryStatusResult>) =
            
                let accessor = x.ProjectMgr.Site.GetService(typeof<SVsBuildManagerAccessor>) :?> IVsBuildManagerAccessor
                let noBuildInProgress = not(VsBuildManagerAccessorExtensionMethods.IsInProgress(accessor))
                      
                match (cmd |> int32 |> enum) with 
                //| VsCommands.Delete   // REVIEW needs work to implement: see e.g. RemoveFromProjectFile() RemoveItem() CanRemoveItems() CanDeleteItem() DeleteFromStorage()
                | VSConstants.VSStd97CmdID.ViewCode when guidCmdGroup = VsMenus.guidStandardCommandSet97 -> 
                        
                        result <- result ||| QueryStatusResult.SUPPORTED
                        if noBuildInProgress then 
                            result <- result ||| QueryStatusResult.ENABLED
                        VSConstants.S_OK
                        
                | VSConstants.VSStd97CmdID.ViewForm when guidCmdGroup = VsMenus.guidStandardCommandSet97 -> 
                        if (x.IsFormSubType) then 
                            result <- result ||| QueryStatusResult.SUPPORTED
                        if noBuildInProgress then 
                            result <- result ||| QueryStatusResult.ENABLED
                        VSConstants.S_OK

                | _ when 
                    guidCmdGroup = VsMenus.guidStandardCommandSet2K && 
                    ((cmd |> int32 |> enum) = VSConstants.VSStd2KCmdID.EXCLUDEFROMPROJECT) ->
                        
                        result <- result ||| QueryStatusResult.SUPPORTED
                        if noBuildInProgress then 
                            result <- result ||| QueryStatusResult.ENABLED
                        VSConstants.S_OK

                | _ when 
                     (guidCmdGroup = VSProjectConstants.guidFSharpProjectCmdSet) &&
                     (cmd = (uint32)VSProjectConstants.MoveUpCmd.ID) -> 

                        result <- result ||| QueryStatusResult.SUPPORTED
                        if noBuildInProgress && root.GetSelectedNodes().Count < 2 && FSharpFileNode.CanMoveUp(x) then
                            result <- result ||| QueryStatusResult.ENABLED
                        VSConstants.S_OK
                        
                | _ when 
                     (guidCmdGroup = VSProjectConstants.guidFSharpProjectCmdSet) &&
                     (cmd = (uint32)VSProjectConstants.MoveDownCmd.ID) -> 

                        result <- result ||| QueryStatusResult.SUPPORTED
                        if noBuildInProgress && root.GetSelectedNodes().Count < 2 && FSharpFileNode.CanMoveDown(x) then
                            result <- result ||| QueryStatusResult.ENABLED
                        VSConstants.S_OK
                        
                | _ when 
                     (guidCmdGroup = VSProjectConstants.guidFSharpProjectCmdSet) &&
                     (cmd = (uint32)VSProjectConstants.AddExistingItemAbove.ID) -> 

                        result <- result ||| QueryStatusResult.SUPPORTED
                        if noBuildInProgress && root.GetSelectedNodes().Count < 2 then
                            result <- result ||| QueryStatusResult.ENABLED
                        VSConstants.S_OK

                | _ when 
                     (guidCmdGroup = VSProjectConstants.guidFSharpProjectCmdSet) &&
                     (cmd = (uint32)VSProjectConstants.AddNewItemAbove.ID) -> 

                        result <- result ||| QueryStatusResult.SUPPORTED
                        if noBuildInProgress && root.GetSelectedNodes().Count < 2 then
                            result <- result ||| QueryStatusResult.ENABLED
                        VSConstants.S_OK
                        
                | _ when 
                     (guidCmdGroup = VSProjectConstants.guidFSharpProjectCmdSet) &&
                     (cmd = (uint32)VSProjectConstants.AddExistingItemBelow.ID) -> 

                        result <- result ||| QueryStatusResult.SUPPORTED
                        if noBuildInProgress && root.GetSelectedNodes().Count < 2 then
                            result <- result ||| QueryStatusResult.ENABLED
                        VSConstants.S_OK

                | _ when 
                     (guidCmdGroup = VSProjectConstants.guidFSharpProjectCmdSet) &&
                     (cmd = (uint32)VSProjectConstants.AddNewItemBelow.ID) -> 

                        result <- result ||| QueryStatusResult.SUPPORTED
                        if noBuildInProgress && root.GetSelectedNodes().Count < 2 then
                            result <- result ||| QueryStatusResult.ENABLED
                        VSConstants.S_OK
                        
                | _ -> base.QueryStatusOnNode(guidCmdGroup, cmd, pCmdText, &result)

            static member CanMoveDown(node : HierarchyNode) =
                if Object.ReferenceEquals(node.NextSibling, null) then
                    false  // can't MoveDown if bottom element
                else
                    match node.NextSibling with
                    | :? FSharpFileNode -> true
                    | :? FSharpFolderNode -> true
                    | :? FileNode -> Debug.Assert(false, "FileNode that's not FSharpFileNode"); true
                    | :? FolderNode -> Debug.Assert(false, "FolderNode that's not FSharpFolderNode"); true
                    | _ -> Debug.Assert(false, "should never happen"); false

            static member CanMoveUp(node : HierarchyNode) =
                if Object.ReferenceEquals(node.Parent.FirstChild, node) then
                    false  // can't MoveUp if top element
                else
                    let mutable prev = node.Parent.FirstChild
                    while not (Object.ReferenceEquals(prev.NextSibling, node)) do
                        prev <- prev.NextSibling
                    match prev with
                    | :? FSharpFileNode -> true
                    | :? FSharpFolderNode -> true
                    | :? FileNode -> Debug.Assert(false, "FileNode that's not FSharpFileNode"); true
                    | :? FolderNode -> Debug.Assert(false, "FolderNode that's not FSharpFolderNode"); true
                    | _ -> false   // can't move up if node above is not a file/folder node (e.g. 'References')

            static member MoveDown(node : HierarchyNode, root : FSharpProjectNode) =
                Debug.Assert(FSharpFileNode.CanMoveDown(node), "Tried to MoveDown when cannot")

                // check-out project file
                if not (root.QueryEditProjectFile false) then 
                    raise (System.Runtime.InteropServices.Marshal.GetExceptionForHR VSConstants.OLE_E_PROMPTSAVECANCELLED)

                let nextItem = 
                    protectVisualState root node <| fun () -> 
                        let parent = node.Parent
                        // delete existing node (remove it from the solution explorer)
                        node.OnItemDeleted()

                        let nextItem =
                            // Move it down in the solution explorer
                            if Object.ReferenceEquals(node.Parent.FirstChild, node) then
                                // TODO: review - i'm not sure that node.Parent.FirstChild can ever == node because
                                // reference folder will always be project's first child, and we don't have support
                                // for folders.
                                let oldNext = node.NextSibling 
                                node.NextSibling <- node.NextSibling.NextSibling
                                oldNext.NextSibling <- node
                                node.Parent.FirstChild <- oldNext
                                if Object.ReferenceEquals(node.Parent.LastChild, oldNext) then
                                    node.Parent.LastChild <- node
                                oldNext
                            else
                                let mutable prev = node.Parent.FirstChild
                                while not( Object.ReferenceEquals(prev.NextSibling, node) ) do
                                    prev <- prev.NextSibling
                                let oldNext = node.NextSibling 
                                node.NextSibling <- node.NextSibling.NextSibling
                                oldNext.NextSibling <- node
                                prev.NextSibling <- oldNext
                                if Object.ReferenceEquals(node.Parent.LastChild, oldNext) then
                                    node.Parent.LastChild <- node
                                oldNext
                        // add item to the solution explorer
                        parent.OnItemAdded(parent, node)
                        nextItem

                // Move it down in the .fsproj file
                match node with
                | :? FSharpFileNode ->   MSBuildUtilities.MoveFileDown(node, nextItem, root)
                | :? FSharpFolderNode -> MSBuildUtilities.MoveFolderDown(node, nextItem, root)
                | _ -> Debug.Assert(false, "should never get here"); raise <| new InvalidOperationException()
                root.SetProjectFileDirty(true)
                // Recompute & notify of changes
                root.ComputeSourcesAndFlags()

            static member MoveUp(node : HierarchyNode, root : FSharpProjectNode) =
                Debug.Assert(FSharpFileNode.CanMoveUp(node), "Tried to MoveUp when cannot")

                // check-out project file
                if not (root.QueryEditProjectFile false) then 
                    raise (System.Runtime.InteropServices.Marshal.GetExceptionForHR VSConstants.OLE_E_PROMPTSAVECANCELLED)

                let prevItem = 
                    protectVisualState root node <| fun () ->
                        let parent = node.Parent
                        // delete existing node (remove it from the solution explorer)
                        node.OnItemDeleted()

                        let prevItem = 
                            // Move it up in the solution explorer
                            if Object.ReferenceEquals(node.Parent.FirstChild.NextSibling, node) then
                                // TODO: review - i'm not sure that node.Parent.FirstChild can ever == node because
                                // reference folder will always be project's first child, and we don't have support
                                // for folders.
                                let oldPrev = node.Parent.FirstChild
                                node.Parent.FirstChild <- node
                                let tmp = node.NextSibling
                                node.NextSibling <- oldPrev
                                oldPrev.NextSibling <- tmp
                                if Object.ReferenceEquals(node.Parent.LastChild, node) then
                                    node.Parent.LastChild <- oldPrev
                                oldPrev
                            else
                                let mutable prevPrev = node.Parent.FirstChild
                                while not( Object.ReferenceEquals(prevPrev.NextSibling.NextSibling, node) ) do
                                    prevPrev <- prevPrev.NextSibling
                                let oldPrev = prevPrev.NextSibling
                                prevPrev.NextSibling <- node
                                let tmp = node.NextSibling
                                node.NextSibling <- oldPrev
                                oldPrev.NextSibling <- tmp
                                if Object.ReferenceEquals(node.Parent.LastChild, node) then
                                    node.Parent.LastChild <- oldPrev
                                oldPrev
                
                        // add node to the solution explorer
                        parent.OnItemAdded(parent, node)
                        prevItem
                
                // Move it up in the .fsproj file
                match node with
                | :? FSharpFileNode ->   MSBuildUtilities.MoveFileUp(node, prevItem, root)
                | :? FSharpFolderNode -> MSBuildUtilities.MoveFolderUp(node, prevItem, root)
                | _ -> Debug.Assert(false, "should never get here"); raise <| new InvalidOperationException()
                root.SetProjectFileDirty(true)
                // Recompute & notify of changes
                root.ComputeSourcesAndFlags()

            member x.GetRelativePath() = 
                let mutable relativePath = Path.GetFileName(x.ItemNode.GetMetadata(ProjectFileConstants.Include))
                let mutable  parent = x.Parent
                while (parent <> null && not (parent :? ProjectNode)) do
                    relativePath <- Path.Combine(parent.Caption, relativePath)
                    parent <- parent.Parent
                relativePath

            member x.ServiceCreator : OleServiceProvider.ServiceCreatorCallback =
                new OleServiceProvider.ServiceCreatorCallback(x.CreateServices)

            member x.CreateServices(serviceType:Type ) =
                if (typeof<EnvDTE.ProjectItem> = serviceType) then 
                    x.GetAutomationObject()
                else 
                    null

    /// Knows about special requirements for project to project references
    type internal FSharpProjectReferenceNode = 
                inherit ProjectReferenceNode 
                new(root:ProjectNode, element:ProjectElement) =
                    { inherit ProjectReferenceNode(root, element) }

                new(project:ProjectNode, referencedProjectName:string, projectPath:string, projectReference:string) =
                    { inherit ProjectReferenceNode(project, referencedProjectName, projectPath, projectReference) }

                /// Evaluates all fsharpfilenode children of the project and returns true if anyone has subtype set to Form
            
                /// <returns>true if a fsharpfilenode with subtype Form is found</returns>
                member private x.HasFormItems() = 
                    //Get the first available py file in the project and update the MainFile property
                    let fsharpFileNodes = new List<FSharpFileNode>()
                    x.ProjectMgr.FindNodesOfType<FSharpFileNode>(fsharpFileNodes)
                    fsharpFileNodes |> Seq.exists (fun node -> node.IsFormSubType)

            
                /// Gets a Project type string for a specified project instance guid
            
                /// <param name="projectGuid">Project instance guid.</param>
                /// <returns>The project type string</returns>
                member private x.GetProjectType(projectGuid:Guid) =
                    let hierarchy = VsShellUtilities.GetHierarchy(x.ProjectMgr.Site, projectGuid)
                    let mutable projectType : obj = null
                    ErrorHandler.ThrowOnFailure(hierarchy.GetProperty(VSConstants.VSITEMID_ROOT, int32 __VSHPROPID.VSHPROPID_TypeName, &projectType)) |> ignore
                    projectType :?> string
            

    /// Reference container node for FSharp references.
    type internal FSharpReferenceContainerNode(project:FSharpProjectNode) = 
            inherit ReferenceContainerNode(project :> ProjectNode)
            
            override x.AddChild(c) =
                let r = base.AddChild(c)
                project.ComputeSourcesAndFlags()
                r

            override x.CreateProjectReferenceNode(element:ProjectElement) =
                (new FSharpProjectReferenceNode(x.ProjectMgr, element) :> ProjectReferenceNode)
            
            override x.CreateProjectReferenceNode(selectorData:VSCOMPONENTSELECTORDATA) =
                (new FSharpProjectReferenceNode(x.ProjectMgr, selectorData.bstrTitle, selectorData.bstrFile, selectorData.bstrProjRef) :> ProjectReferenceNode)

    type internal SelectionElementValueChangedListener(serviceProvider:Microsoft.VisualStudio.Shell.ServiceProvider) =
            inherit SelectionListener(serviceProvider)

            override x.OnElementValueChanged(_elementid, _varValueOld, _varValueNew) = VSConstants.S_OK

