namespace Factory.Core

open System
open System.Text.RegularExpressions

/// The one source of facts about the UI contract's protected files and the commands that change them (docs/ui-contract-verify.md §3).
/// RoleAllowListPolicy, AcsToolPolicy (as derived args for its yaml rules) and ScopedWorkspace all ask here, so the three cannot disagree.
/// Reference: the kit hook's test table. Pure and conservative: a path or command it cannot place is not a fact, and the sandbox and the
/// workspace's own path checks still apply.
[<RequireQualifiedAccess>]
module UiPolicyFacts =

    let private contract = "ui-contract"
    let private protectedFolders = [ "previews"; "specs"; "manifests"; "preview"; "tests"; "holdout" ]
    let private anchors = [ "ui-contract"; "ui"; ".claude" ]

    let private isAbsolute (p: string) = p.StartsWith "/" || Regex.IsMatch(p, @"^[A-Za-z]:")

    let private collapse (path: string) =
        let stack = Collections.Generic.Stack<string>()
        for seg in path.Replace('\\', '/').Split('/') do
            match seg with
            | "" | "." -> ()
            | ".." -> if stack.Count > 0 then stack.Pop() |> ignore
            | s -> stack.Push s
        stack.ToArray() |> Array.rev |> List.ofArray

    /// Every relative reading of an absolute path: the tail from each anchor segment. A relative path has exactly one.
    let private readings (path: string) : string list list =
        let segs = collapse path
        if not (isAbsolute (path.Replace('\\', '/'))) then [ segs ]
        else
            let tails =
                [ for i, s in List.indexed segs do
                      if List.contains (s.ToLowerInvariant()) anchors then yield List.skip i segs ]
            let config =
                match List.tryLast segs with
                | Some f when f.StartsWith("vitest.config.", StringComparison.OrdinalIgnoreCase) -> [ [ f ] ]
                | _ -> []
            segs :: (tails @ config)

    /// `\` becomes `/`, `./` goes, `..` collapses against a virtual root (it cannot climb above it); an absolute path keeps its tail after
    /// the last `ui-contract/`, `ui/` or `.claude/`, so a path written absolutely lands on the same repo-relative name.
    let normalise (path: string) : string =
        let segs = collapse path
        let tails =
            if not (isAbsolute (path.Replace('\\', '/'))) then []
            else [ for i, s in List.indexed segs do if List.contains (s.ToLowerInvariant()) anchors then yield List.skip i segs ]
        String.Join("/", (match tails with [] -> segs | _ -> List.minBy List.length tails))

    let private protectedReading (segs: string list) =
        match segs |> List.map (fun s -> s.ToLowerInvariant()) with
        | [ c ] when c = contract -> true
        | c :: f :: _ when c = contract && List.contains f protectedFolders -> true
        | [ "ui"; "holdout" ] | "ui" :: "holdout" :: _ -> true
        | [ f ] when f.StartsWith "vitest.config." -> true
        | ".claude" :: "hooks" :: _ -> true
        | _ -> false

    let private holdoutReading (segs: string list) =
        match segs |> List.map (fun s -> s.ToLowerInvariant()) with
        | c :: "holdout" :: _ when c = contract -> true
        | "ui" :: "holdout" :: _ -> true
        | _ -> false

    /// `ui-contract/{previews,specs,manifests,preview,tests,holdout}/**`, `ui/holdout/**`, root `vitest.config.*`, `.claude/hooks/**`, or the
    /// `ui-contract` folder itself (deleting it removes them all). Build roles may not write or delete these.
    let isUiProtected (path: string) : bool = readings path |> List.exists protectedReading

    /// `ui-contract/holdout/**` and `ui/holdout/**` (and the folders themselves): no role reads or lists them.
    let isHoldout (path: string) : bool = readings path |> List.exists holdoutReading

    // ---- commands ----------------------------------------------------------------------------------------------------------------

    /// Command lines split into the segments a shell would run separately: a newline, `;`, `&&`, `||` or `|`.
    let private segments (command: string) =
        Regex.Split(command.ToLowerInvariant(), @"\r?\n|\r|;|&&|\|\||\||&")
        |> Array.map (fun s -> s.Trim())
        |> Array.filter (fun s -> s <> "")
        |> List.ofArray

    let private tokens (segment: string) =
        Regex.Split(segment, @"[\s>()<'""`,]+") |> Array.filter (fun t -> t <> "") |> List.ofArray

    let private isUpdateFlag (t: string) =
        t = "-u" || t = "update" || t = "--update" || t.StartsWith "--update=" || Regex.IsMatch(t, @"^-[a-z]*u[a-z]*$")

    /// A `vitest` invocation with `-u`, `--update` or an `update` argument, or `npm run ui:preview` (with or without `-- <args>`), on any line or
    /// `&&`/`;` segment. `git push -u` and `sort -u` are not vitest, so they are not updates.
    let updatesSnapshots (command: string) : bool =
        segments command
        |> List.exists (fun seg ->
            let ts = tokens seg
            let vitest = ts |> List.exists (fun t -> t = "vitest" || t.EndsWith "/vitest" || t.EndsWith "\\vitest")
            (vitest && ts |> List.exists isUpdateFlag) || ts |> List.exists (fun t -> t = "ui:preview"))

    let private changeVerbs =
        set [ "rm"; "mv"; "cp"; "tee"; "truncate"; "del"; "rd"; "rmdir"; "remove-item"; "move-item"; "copy-item"; "set-content"; "add-content"
              "out-file"; "new-item"; "touch"; "ln"; "dd"; "install"; "patch"; "rename-item"; "ri"; "mi"; "ci"; "erase" ]

    let private gitChanges = set [ "checkout"; "restore"; "reset"; "stash"; "apply"; "am"; "cherry-pick"; "revert"; "rm"; "mv"; "clean" ]

    let private changes (seg: string) (ts: string list) =
        seg.Contains ">"
        || ts |> List.exists (fun t -> changeVerbs.Contains t)
        || (ts |> List.exists (fun t -> t = "sed" || t = "perl") && ts |> List.exists (fun t -> Regex.IsMatch(t, @"^-[a-z]*i[a-z]*(\..*)?$")))
        || (List.contains "git" ts && ts |> List.exists (fun t -> gitChanges.Contains t))

    /// A command that reaches the holdout in any way, or changes (rm, mv, cp, sed -i, a redirect, git checkout ...) a UI-protected path, also
    /// from inside `ui-contract` after a `cd`. Reading approved previews and specs (`ls`, `cat`) is not touching.
    let touchesUiContract (command: string) : bool =
        let mutable prefix = ""
        let mutable hit = false
        for seg in segments command do
            let ts = tokens seg
            let withPrefix t = if prefix = "" then t else prefix + "/" + t
            let paths = ts |> List.collect (fun t -> t :: (match t.Split('=') with [| _; v |] when v <> "" -> [ v ] | _ -> []))
            let unix = seg.Replace('\\', '/')
            if paths |> List.exists (fun t -> isHoldout t || isHoldout (withPrefix t)) || unix.Contains "holdout/" then hit <- true
            if changes seg ts && paths |> List.exists (fun t -> isUiProtected t || (prefix <> "" && isUiProtected (withPrefix t))) then hit <- true
            match ts with
            | ("cd" | "pushd" | "set-location") :: target :: _ ->
                let n = normalise target
                prefix <- if n = contract || n.StartsWith(contract + "/") then n else ""
            | _ -> ()
        hit
