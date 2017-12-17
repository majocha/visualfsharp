namespace Microsoft.VisualStudio.FSharp.Editor

open System
open System.Collections.Concurrent
open System.ComponentModel.Composition
open System.Runtime.InteropServices

open Microsoft.VisualStudio.Settings
open Microsoft.VisualStudio.Shell
open Microsoft.VisualStudio.ComponentModelHost

open Newtonsoft.Json

module internal SettingsPersistence =
    open Newtonsoft.Json.Linq

    // Each group of settings is a value of some named type, for example 'IntelliSenseOptions', 'QuickInfoOptions'
    // We cache exactly one instance of each, treating them as immutable.
    // This cache is updated by the SettingsStore when the user changes an option.
    let private cache = ConcurrentDictionary<Type, obj>()

    let getSettings() : 't =
        match cache.TryGetValue(typeof<'t>) with
        | true, value -> value :?> 't
        | _ -> failwithf "Settings %s are not registered." typeof<'t>.Name

    let setSettings( settings: 't) =
        cache.[typeof<'t>] <- settings

    let key (t: Type) = t.Namespace + "_" + t.Name

    let toJson() =
        cache
        |> Seq.map (fun kv -> key kv.Key, kv.Value)
        |> Map.ofSeq
        |> JsonConvert.SerializeObject

    let fromJson v = 
        try
            let jtoken = JToken.Parse v
            for k in cache.Keys do
                try
                    let mutable setting = cache.[k]
                    let partial = jtoken.[key k].ToString()
                    JsonConvert.PopulateObject(partial, setting)
                    cache.[k] <- setting
                with _ -> ()
        with _ -> () // no valid json

    // marker interface for default settings export
    type ISettings = interface end

    [<ComVisible(true)>]
    type AutomationObject(serviceProvider: IComponentModel) =
        do serviceProvider.GetService<ISettings>() |> ignore
        member __.FSharpSettings with get() = toJson() and set v = fromJson v

    [<Guid(Guids.svsSettingsPersistenceManagerIdString)>]
    type SVsSettingsPersistenceManager = class end

    [<Export>]
    type SettingsStore
        [<ImportingConstructor>]
        (
            [<Import(typeof<SVsServiceProvider>)>] 
            serviceProvider: IServiceProvider
        ) =
        let settingsManager = serviceProvider.GetService(typeof<SVsSettingsPersistenceManager>) :?> ISettingsManager

        // settings quallified type names are used as keys, this should be enough to avoid collisions
        let storageKey (typ: Type) = typ.Namespace + "." + typ.Name

        let save (settings: 't) =
            // we replace default serialization with Newtonsoft.Json for easy schema evolution
            settingsManager.SetValueAsync(storageKey typeof<'t>, JsonConvert.SerializeObject settings, false)
            |> Async.AwaitTask 

        let tryPopulate (settings: 't) =
            let result, json = settingsManager.TryGetValue(storageKey typeof<'t>)
            if result = GetValueResult.Success then
                // if it fails we just return what we got
                try JsonConvert.PopulateObject(json, settings) with _ -> () 
            settings       

        let ensureTrackingChanges (settings: 't) =                       
            settings |> tryPopulate |> setSettings
            let subset = settingsManager.GetSubset(storageKey typeof<'t>)
            subset.add_SettingChangedAsync 
            <| PropertyChangedAsyncEventHandler (fun _ _ ->
                (getSettings() : 't) |> tryPopulate |> setSettings
                System.Threading.Tasks.Task.CompletedTask )

        member __.LoadSettings() : 't =
            getSettings() |> tryPopulate
            
        member __.SaveSettings(settings: 't) =
            save settings 

        member __.RegisterDefault(defaultValue: 't) =
            ensureTrackingChanges defaultValue