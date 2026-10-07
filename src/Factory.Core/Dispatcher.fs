namespace Factory.Core

open System.Collections.Generic

type TeamId =
    | TeamId of string
    member this.Value = let (TeamId v) = this in v

/// Tasks that may touch the same files. A team works in one worktree, one task at a time,
/// in dependency order. Teams never overlap each other's writes.
type Team =
    { Id: TeamId
      Tasks: PlannedTask list
      /// The team's whole footprint: every task's Writes and TestWrites.
      Writes: PathPattern list
      Elements: ElementId list }

/// Teams whose cross-team dependencies are all satisfied by earlier waves; they run in parallel.
type Wave = { Index: int; Teams: Team list }

type DispatchPlan = { HandoverId: string; Waves: Wave list }

[<RequireQualifiedAccess>]
module Dispatcher =

    /// Everything a task may write, production and test paths alike.
    let private footprint (t: PlannedTask) = t.Writes @ t.TestWrites

    let private tasksOverlap (a: PlannedTask) (b: PlannedTask) =
        footprint a |> List.exists (fun wa -> footprint b |> List.exists (PathPattern.mayOverlap wa))

    /// Tarjan's strongly connected components over nodes 0..n-1.
    let private scc (n: int) (succ: int -> int seq) : int list list =
        let index = Array.create n -1
        let low = Array.zeroCreate n
        let onStack = Array.zeroCreate n
        let stack = Stack<int>()
        let mutable next = 0
        let result = ResizeArray<int list>()

        let rec strong v =
            index.[v] <- next
            low.[v] <- next
            next <- next + 1
            stack.Push v
            onStack.[v] <- true

            for w in succ v do
                if index.[w] = -1 then
                    strong w
                    low.[v] <- min low.[v] low.[w]
                elif onStack.[w] then
                    low.[v] <- min low.[v] index.[w]

            if low.[v] = index.[v] then
                let mutable comp = []
                let mutable fin = false
                while not fin do
                    let w = stack.Pop()
                    onStack.[w] <- false
                    comp <- w :: comp
                    fin <- (w = v)
                result.Add comp

        for v in 0 .. n - 1 do
            if index.[v] = -1 then strong v

        List.ofSeq result

    /// Kahn's algorithm, always taking the smallest ready key first so the order is stable.
    let private topoBy (key: 'a -> string) (deps: 'a -> 'a list) (items: 'a list) : 'a list =
        let set = HashSet<'a>(items)
        let remaining = Dictionary<'a, int>()
        let dependants = Dictionary<'a, ResizeArray<'a>>()

        for i in items do
            let ds = deps i |> List.filter set.Contains |> List.distinct
            remaining.[i] <- ds.Length
            for d in ds do
                if not (dependants.ContainsKey d) then dependants.[d] <- ResizeArray()
                dependants.[d].Add i

        let ready = SortedSet<string * int>()
        let byIdx = items |> List.toArray

        byIdx |> Array.iteri (fun ix i -> if remaining.[i] = 0 then ready.Add((key i, ix)) |> ignore)

        let lookup = byIdx |> Array.mapi (fun ix i -> i, ix) |> dict
        let out = ResizeArray<'a>()

        while ready.Count > 0 do
            let (k, ix) = ready.Min
            ready.Remove((k, ix)) |> ignore
            let i = byIdx.[ix]
            out.Add i
            match dependants.TryGetValue i with
            | true, ds ->
                for d in ds do
                    remaining.[d] <- remaining.[d] - 1
                    if remaining.[d] = 0 then ready.Add((key d, lookup.[d])) |> ignore
            | _ -> ()

        if out.Count <> items.Length then failwith "cycle in topoBy input (validator should have rejected it)"
        List.ofSeq out

    /// RESOURCES.md §4. Spec: every test starting "dispatcher:".
    /// Helpers provided: tasksOverlap, scc (Tarjan), topoBy (stable Kahn). Steps:
    ///   1. Sort tasks by id (determinism). Union-find: union every pair whose writes may overlap.
    ///   2. Build the graph between groups from DependsOn (edge: group of dep -> group of task).
    ///      Groups in the same strongly connected component must become one team: union them.
    ///      (A1 -> B1 -> A2 with A1/A2 overlapping is the test case.)
    ///   3. Each group is a Team: tasks in dependency order (topoBy), id "team-" + smallest task id,
    ///      Writes/Elements = distinct union.
    ///   4. Waves = layers of the team DAG (Kahn by level): wave 0 = teams with no incoming edge...
    ///      Sort teams inside a wave by id.
    /// Invariant worth a property test later (FsCheck): no two teams ever have overlapping writes.
    let dispatch (vp: ValidatedPlan) : DispatchPlan =
        let tasks = vp.Plan.Tasks |> List.sortBy (fun t -> t.Id.Value) |> List.toArray
        let n = tasks.Length

        // Union-find. The root of a set is always its smallest index, i.e. its smallest task id.
        let parent = Array.init n id
        let rec find i =
            if parent.[i] = i then i
            else
                let r = find parent.[i]
                parent.[i] <- r
                r
        let union a b =
            let ra, rb = find a, find b
            if ra <> rb then parent.[max ra rb] <- min ra rb

        for i in 0 .. n - 1 do
            for j in i + 1 .. n - 1 do
                if tasksOverlap tasks.[i] tasks.[j] then union i j

        let index = tasks |> Array.mapi (fun i t -> t.Id, i) |> dict
        let depsOf i =
            tasks.[i].DependsOn |> List.choose (fun d -> match index.TryGetValue d with | true, j -> Some j | _ -> None)
        let groupEdges () =
            [ for i in 0 .. n - 1 do
                  for j in depsOf i do
                      let from, into = find j, find i
                      if from <> into then (from, into) ]
            |> List.distinct

        // Groups on a cycle can't be ordered into waves: they become one team. The condensation of
        // a graph is acyclic, so one pass is enough.
        let edges = groupEdges ()
        let succ g = edges |> Seq.filter (fun (a, _) -> a = g) |> Seq.map snd
        for comp in scc n succ do
            match comp with
            | first :: rest -> for r in rest do union first r
            | [] -> ()

        let teams =
            [ 0 .. n - 1 ]
            |> List.groupBy find
            |> List.map (fun (root, members) ->
                let ts = members |> List.map (fun i -> tasks.[i])
                let ids = ts |> List.map (fun t -> t.Id) |> Set.ofList
                let ordered = ts |> topoBy (fun t -> t.Id.Value) (fun t -> ts |> List.filter (fun x -> List.contains x.Id t.DependsOn && ids.Contains x.Id))
                root,
                { Id = TeamId("team-" + tasks.[root].Id.Value)
                  Tasks = ordered
                  Writes = ts |> List.collect footprint |> List.distinct
                  Elements = ts |> List.collect (fun t -> t.Elements) |> List.distinct })
            |> Map.ofList

        // Wave = longest path from a source in the team DAG.
        let teamEdges = groupEdges ()
        let level = Dictionary<int, int>()
        let rec levelOf g =
            match level.TryGetValue g with
            | true, l -> l
            | _ ->
                let l =
                    match teamEdges |> List.filter (fun (_, into) -> into = g) with
                    | [] -> 0
                    | incoming -> 1 + (incoming |> List.map (fst >> levelOf) |> List.max)
                level.[g] <- l
                l

        let waves =
            teams
            |> Map.toList
            |> List.groupBy (fst >> levelOf)
            |> List.sortBy fst
            |> List.mapi (fun i (_, ts) -> { Index = i; Teams = ts |> List.map snd |> List.sortBy (fun t -> t.Id.Value) })

        { HandoverId = vp.Handover.Id; Waves = waves }
