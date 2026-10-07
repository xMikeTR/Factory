namespace Factory.Core

open System
open System.IO
open System.Xml
open System.Xml.Linq

/// What an agent may change in the files `dotnet restore` reads before the factory restores with
/// the network on. Restore evaluates MSBuild: a target, an import, a custom SDK or a property
/// function in a project file would run with network, and a package-source property would fetch
/// from anywhere. So a change is checked against an allow-list (packages, project references,
/// compile items, harmless properties). What the repo already had is trusted: only elements that
/// are new or changed against `before` are checked.
[<RequireQualifiedAccess>]
module ProjectFileChange =

    let private sdks =
        set [ "Microsoft.NET.Sdk"; "Microsoft.NET.Sdk.Web"; "Microsoft.NET.Sdk.Worker"; "Microsoft.NET.Sdk.Razor"
              "Microsoft.NET.Sdk.BlazorWebAssembly" ]

    let private rootAttributes = set [ "Sdk"; "xmlns"; "ToolsVersion" ]
    let private groupAttributes = set [ "Condition"; "Label" ]

    let private properties =
        set [ "TargetFramework"; "TargetFrameworks"; "Nullable"; "ImplicitUsings"; "IsPackable"; "IsTestProject"
              "IsPublishable"; "OutputType"; "RootNamespace"; "AssemblyName"; "LangVersion"; "TreatWarningsAsErrors"
              "WarningsAsErrors"; "NoWarn"; "WarningLevel"; "GenerateDocumentationFile"; "InvariantGlobalization"
              "UserSecretsId"; "Description"; "PreserveCompilationContext"; "GenerateProgramFile"
              "ManagePackageVersionsCentrally"; "CentralPackageTransitivePinningEnabled"; "EnableDefaultCompileItems"
              "Version"; "AssemblyVersion"; "FileVersion"; "ServerGarbageCollection"; "AllowUnsafeBlocks"
              "Optimize"; "DebugType"; "Deterministic"; "SatelliteResourceLanguages" ]

    let private items =
        set [ "PackageReference"; "PackageVersion"; "ProjectReference"; "FrameworkReference"; "Compile"; "None"
              "Content"; "EmbeddedResource"; "Using"; "InternalsVisibleTo"; "Folder" ]

    /// Item attributes, and the same names as metadata elements under an item.
    let private metadata =
        set [ "Include"; "Remove"; "Update"; "Condition"; "Version"; "VersionOverride"; "PrivateAssets"; "IncludeAssets"
              "ExcludeAssets"; "CopyToOutputDirectory"; "CopyToPublishDirectory"; "Link"; "Alias"; "Aliases"
              "Static"; "ReferenceOutputAssembly" ]

    let private parse (text: string) : Result<XDocument, string> =
        let settings = XmlReaderSettings(DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null)
        try
            use reader = XmlReader.Create(new StringReader(text), settings)
            Ok(XDocument.Load reader)
        with :? XmlException as e -> Error $"not well-formed XML: {e.Message}"

    let private name (e: XElement) = e.Name.LocalName
    let private path (e: XElement) = e.AncestorsAndSelf() |> Seq.rev |> Seq.map name |> String.concat "/"

    /// An element as it reads on its own: where it sits, its attributes, its text if it is a leaf.
    /// A container's children are separate entries, so adding an item does not make its group new.
    let private canonical (e: XElement) =
        let attrs = e.Attributes() |> Seq.map (fun a -> $"{a.Name.LocalName}={a.Value}") |> Seq.sort |> String.concat ";"
        let text = if e.HasElements then "" else e.Value.Trim()
        $"{path e}[{attrs}]{text}"

    /// MSBuild property functions call .NET (`$([System.IO.File]::...)`) and read the registry.
    let private runsCode (value: string) = value.Contains "$([" || value.Contains "$(Registry:"

    let private elementErrors (e: XElement) : string list =
        let depth = e.Ancestors() |> Seq.length
        let parent = if depth = 0 then "" else name e.Parent
        let attrs = e.Attributes() |> Seq.filter (fun a -> not a.IsNamespaceDeclaration) |> List.ofSeq
        let badAttrs allowed =
            [ for a in attrs do
                  if not (Set.contains a.Name.LocalName allowed) then
                      $"attribute {a.Name.LocalName} on <{name e}> is not on the restore allow-list" ]
        let functions =
            [ for v in (if e.HasElements then [] else [ e.Value ]) @ (attrs |> List.map (fun a -> a.Value)) do
                  if runsCode v then $"<{name e}> uses an MSBuild property function, which runs code during restore: {v.Trim()}" ]
        let placement =
            match depth with
            | 0 when name e <> "Project" -> [ $"the root element must be <Project>, not <{name e}>" ]
            | 0 ->
                badAttrs rootAttributes
                @ [ match e.Attribute(XName.Get "Sdk") with
                    | null -> ()
                    | sdk when not (Set.contains sdk.Value sdks) ->
                        let known = String.concat ", " sdks
                        $"Sdk=\"{sdk.Value}\" is not one of the SDKs the factory restores ({known})"
                    | _ -> () ]
            | 1 when name e = "PropertyGroup" || name e = "ItemGroup" -> badAttrs groupAttributes
            | 1 -> [ $"<{name e}> is not allowed in a project file the factory restores (Target, Import, UsingTask and the like run during restore)" ]
            | 2 when parent = "PropertyGroup" && Set.contains (name e) properties -> badAttrs (set [ "Condition" ])
            | 2 when parent = "PropertyGroup" -> [ $"property {name e} is not on the restore allow-list" ]
            | 2 when parent = "ItemGroup" && Set.contains (name e) items -> badAttrs metadata
            | 2 -> [ $"item <{name e}> is not on the restore allow-list" ]
            | 3 when Set.contains (name e) metadata && not e.HasElements -> badAttrs (set [ "Condition" ])
            | _ -> [ $"<{name e}> under <{parent}> is not on the restore allow-list" ]
        placement @ functions

    /// Ok when every element of `after` that is not already in `before` is on the allow-list.
    /// `before` is None for a new file.
    let check (before: string option) (after: string) : Result<unit, string list> =
        match parse after with
        | Error e -> Error [ e ]
        | Ok doc ->
            let known =
                match before |> Option.map parse with
                | Some(Ok old) -> old.Root.DescendantsAndSelf() |> Seq.map canonical |> Set.ofSeq
                | _ -> Set.empty
            match
                doc.Root.DescendantsAndSelf()
                |> Seq.filter (fun e -> not (Set.contains (canonical e) known))
                |> Seq.collect elementErrors
                |> Seq.distinct
                |> List.ofSeq
            with
            | [] -> Ok()
            | errors -> Error errors

    let private fileName (path: string) = Path.GetFileName(path.Replace('\\', '/')).ToLowerInvariant()

    /// An MSBuild project file (.csproj, .fsproj...): what `dotnet restore` is pointed at.
    let isProject (file: string) =
        let ext = Path.GetExtension file
        ext.Length > 5 && ext.EndsWith "proj"

    /// Whether a repo path is read by `dotnet restore`.
    let affectsRestore (path: string) : bool =
        let file = fileName path
        file = "nuget.config" || file = "global.json" || file.EndsWith ".props" || file.EndsWith ".targets" || isProject file

    /// The rule for one changed file. `after` is None when the file was deleted, which is always fine.
    let review (path: string) (before: string option) (after: string option) : Result<unit, string list> =
        let file = fileName path
        match after with
        | None -> Ok()
        | Some _ when file = "nuget.config" -> Error [ $"{path}: package sources are fixed by the factory; nuget.config cannot change" ]
        | Some _ when file = "global.json" -> Error [ $"{path}: global.json picks the SDK and MSBuild SDK packages; it cannot change in a task" ]
        | Some text when file = "directory.packages.props" || isProject file ->
            check before text |> Result.mapError (List.map (fun e -> $"{path}: {e}"))
        | Some _ when file.EndsWith ".props" || file.EndsWith ".targets" ->
            Error [ $"{path}: .props and .targets files are imported into every restore; they cannot change in a task" ]
        | Some _ -> Ok()
