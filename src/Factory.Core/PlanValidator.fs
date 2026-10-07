namespace Factory.Core

open System
open System.Collections.Generic

type PlanError =
    | EmptyPlan
    | HandoverMismatch of expected: string * actual: string
    | DuplicateTaskId of TaskId
    | NoWrites of TaskId
    | NoElements of TaskId
    | NoAutomatedVerify of TaskId
    | UnknownElement of TaskId * ElementId
    | UnknownDependency of TaskId * TaskId
    | DependencyCycle of TaskId list
    | WriteOutsideElement of TaskId * PathPattern
    | ProtectedWrite of TaskId * PathPattern
    | UnknownDecision of TaskId * DecisionId
    | CoverWithoutBinding of TaskId * DecisionId
    | UncoveredDecision of DecisionId
    | SelfSeededWithoutCleanup of TaskId * verify: string
    | EmptyVerify of TaskId * verify: string
    | InvalidTimeout of TaskId * verify: string

[<RequireQualifiedAccess>]
module PlanError =
    /// Plain-language reasons, written to be fed straight back to the planner agent.
    let describe =
        function
        | EmptyPlan -> "The plan has no tasks."
        | HandoverMismatch(e, a) -> $"The plan targets handover '{a}' but the package is '{e}'. Plan against the package you were given."
        | DuplicateTaskId t -> $"Task id {t.Value} is used more than once."
        | NoWrites t -> $"Task {t.Value} declares no `writes` paths."
        | NoElements t -> $"Task {t.Value} is not linked to any C4 element."
        | NoAutomatedVerify t -> $"Task {t.Value} has no automated verify entry; manual checks alone cannot pass a task."
        | UnknownElement(t, e) -> $"Task {t.Value} references element '{e.Value}', which is not in the handover package."
        | UnknownDependency(t, d) -> $"Task {t.Value} depends on {d.Value}, which does not exist."
        | DependencyCycle ids -> "Tasks depend on each other in a cycle: " + String.Join(" -> ", ids |> List.map (fun (TaskId v) -> v))
        | WriteOutsideElement(t, p) -> $"Task {t.Value} writes '{p.Value}', which is outside the code areas of its elements. Link the right element or narrow the path."
        | ProtectedWrite(t, p) -> $"Task {t.Value} writes '{p.Value}', which overlaps a protected path. Tasks may not change the registry, CI, or verify scripts."
        | UnknownDecision(t, d) -> $"Task {t.Value} covers decision {d.Value}, which is not bound in this package."
        | CoverWithoutBinding(t, d) -> $"Task {t.Value} covers {d.Value} but touches none of the elements that decision binds."
        | UncoveredDecision d -> $"No task covers bound decision {d.Value}. Every bound decision needs at least one task whose verify proves it."
        | SelfSeededWithoutCleanup(t, v) -> $"Task {t.Value}, verify '{v}': self-seeded checks must declare a cleanup."
        | EmptyVerify(t, v) -> $"Task {t.Value}, verify '{v}' has no command or instructions."
        | InvalidTimeout(t, v) -> $"Task {t.Value}, verify '{v}' needs a positive timeoutSeconds."

/// A plan that has passed every rule against a specific handover. The only way to get one is
/// PlanValidator.validate, so anything that takes a ValidatedPlan (the dispatcher, the runner)
/// cannot be handed an unchecked plan.
[<Sealed; AllowNullLiteral>]
type ValidatedPlan internal (plan: Plan, handover: Handover) =
    member _.Plan = plan
    member _.Handover = handover

[<RequireQualifiedAccess>]
module PlanValidator =

    let private isBlank (s: string) = String.IsNullOrWhiteSpace s

    /// First dependency cycle found, as the list of task ids around it (first id repeated at the end).
    ///
    /// RESOURCES.md §3. Spec: "validator: dependency cycle and unknown dep".
    /// Depth-first search with three colours: unvisited, on-the-stack, finished. Reaching a node
    /// that is on the stack means a cycle; the cycle is the stack from that node to here.
    /// Ignore dependencies on unknown ids (validate reports those separately).
    let findCycle (tasks: PlannedTask list) : TaskId list option =
        let deps = tasks |> List.fold (fun m t -> Map.add t.Id t.DependsOn m) Map.empty
        let onStack, finished = HashSet<TaskId>(), HashSet<TaskId>()
        // `path` is the DFS stack, most recent first.
        let rec visit (path: TaskId list) (id: TaskId) : TaskId list option =
            if finished.Contains id then None
            elif onStack.Contains id then
                let fromHere = List.rev path |> List.skipWhile ((<>) id)
                Some(fromHere @ [ id ])
            else
                onStack.Add id |> ignore
                let found = deps[id] |> List.filter deps.ContainsKey |> List.tryPick (visit (id :: path))
                onStack.Remove id |> ignore
                finished.Add id |> ignore
                found
        tasks |> List.tryPick (fun t -> visit [] t.Id)

    /// RESOURCES.md §3. Spec: every test starting "validator:".
    /// Collect ALL errors (don't stop at the first): the list goes back to the planner in one go.
    /// One PlanError case = one rule. Suggested order:
    ///   plan-level:  EmptyPlan, HandoverMismatch (id AND digest), DuplicateTaskId
    ///   per task:    NoWrites, NoElements, NoAutomatedVerify, UnknownElement,
    ///                ProtectedWrite (mayOverlap with any protected path) else
    ///                WriteOutsideElement (not isWithin any code area of the task's elements),
    ///                UnknownDependency, UnknownDecision, CoverWithoutBinding,
    ///                verify entries: EmptyVerify, SelfSeededWithoutCleanup, InvalidTimeout
    ///   whole plan:  UncoveredDecision (every handover decision covered by some task), DependencyCycle
    /// Return Ok(ValidatedPlan(plan, handover)) only when the list is empty. The constructor is
    /// `internal`: this function is the only door into ValidatedPlan.
    let validate (handover: Handover) (plan: Plan) : Result<ValidatedPlan, PlanError list> =
        let elements = handover.Elements |> List.map (fun e -> e.Id, e) |> Map.ofList
        let decisions = handover.Decisions |> List.map (fun d -> d.Id, d) |> Map.ofList
        let taskIds = plan.Tasks |> List.map (fun t -> t.Id) |> Set.ofList

        let planLevel =
            [ if plan.Tasks.IsEmpty then EmptyPlan
              if plan.HandoverId <> handover.Id then HandoverMismatch(handover.Id, plan.HandoverId)
              if plan.HandoverDigest <> handover.Digest then HandoverMismatch(handover.Digest, plan.HandoverDigest)
              for id, n in plan.Tasks |> List.countBy (fun t -> t.Id) do
                  if n > 1 then DuplicateTaskId id ]

        let verifyErrors (t: PlannedTask) (v: VerifyEntry) =
            [ match v with
              | Automated a ->
                  if isBlank a.Run then EmptyVerify(t.Id, a.Name)
                  if a.Kind = SelfSeeded && (a.Cleanup |> Option.forall isBlank) then SelfSeededWithoutCleanup(t.Id, a.Name)
                  if a.TimeoutSeconds <= 0 then InvalidTimeout(t.Id, a.Name)
              | Manual m -> if isBlank m.Instructions then EmptyVerify(t.Id, m.Name) ]

        let taskLevel (t: PlannedTask) =
            let known = t.Elements |> List.choose (fun e -> Map.tryFind e elements)
            let codeAreas = known |> List.collect (fun e -> e.CodeAreas)
            [ if t.Writes.IsEmpty then NoWrites t.Id
              if t.Elements.IsEmpty then NoElements t.Id
              if not (t.Verify |> List.exists (function Automated _ -> true | Manual _ -> false)) then NoAutomatedVerify t.Id
              for e in t.Elements do
                  if not (elements.ContainsKey e) then UnknownElement(t.Id, e)
              for w in t.Writes do
                  if handover.ProtectedPaths |> List.exists (PathPattern.mayOverlap w) then ProtectedWrite(t.Id, w)
                  elif not (codeAreas |> List.exists (PathPattern.isWithin w)) then WriteOutsideElement(t.Id, w)
              for d in t.DependsOn do
                  if not (taskIds.Contains d) then UnknownDependency(t.Id, d)
              for c in t.Covers do
                  match Map.tryFind c decisions with
                  | None -> UnknownDecision(t.Id, c)
                  | Some d -> if not (d.Binds |> List.exists (fun b -> List.contains b t.Elements)) then CoverWithoutBinding(t.Id, c)
              yield! t.Verify |> List.collect (verifyErrors t) ]

        let wholePlan =
            [ for d in handover.Decisions do
                  if not (plan.Tasks |> List.exists (fun t -> List.contains d.Id t.Covers)) then UncoveredDecision d.Id
              match findCycle plan.Tasks with
              | Some cycle -> DependencyCycle cycle
              | None -> () ]

        match planLevel @ (plan.Tasks |> List.collect taskLevel) @ wholePlan with
        | [] -> Ok(ValidatedPlan(plan, handover))
        | errors -> Error errors
