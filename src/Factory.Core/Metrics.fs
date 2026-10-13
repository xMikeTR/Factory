namespace Factory.Core

open System

type WorkTiming = { Median: int64; P90: int64; Samples: int }

/// System 4's view of the work flow, from the registry ledger. Only what the ledger can state:
/// lead time is pull to done (the ledger has no freeze row), and an acceptance first-pass rate needs
/// pass rows designc does not write, so neither is guessed.
type WorkMetrics =
    { /// (ISO week, target kind) -> items Done that week
      Throughput: ((string * string) * int) list
      LeadTime: WorkTiming option
      HumanWait: WorkTiming option
      /// Items still waiting on a human right now
      OpenWaits: int
      ConflictDeferrals: int
      /// stale rechecks / rechecks; None before any re-check ran
      RecheckRate: float option
      EscapesByOrigin: (string * int) list }

[<RequireQualifiedAccess>]
module Metrics =
    let kindOf (t: Target) =
        match t with
        | Repo _ -> "repo"
        | Document _ -> "document"
        | Jira _ -> "jira"
        | Investigation _ -> "investigation"

    let isoWeek (epochMs: int64) =
        let d = DateTimeOffset.FromUnixTimeMilliseconds(epochMs).UtcDateTime
        $"{Globalization.ISOWeek.GetYear d}-W{Globalization.ISOWeek.GetWeekOfYear d:D2}"

    let private timing (xs: int64 list) =
        match List.sort xs with
        | [] -> None
        | s ->
            let at (q: float) = s.[min (s.Length - 1) (int (Math.Ceiling(q * float s.Length)) - 1 |> max 0)]
            Some { Median = at 0.5; P90 = at 0.9; Samples = s.Length }

    /// `done` is the digest's Done list: a claim off main or without evidence is not throughput.
    let compute (items: WorkItem list) (facts: (int64 * LedgerFact) list) (digest: Digest) : WorkMetrics =
        let ev = facts |> List.sortBy fst
        let kinds = items |> List.map (fun i -> i.Id.Value, kindOf i.Target) |> Map.ofList
        let doneAt = digest.Done |> List.map (fun (id, _, ts) -> id, ts)

        let throughput =
            doneAt
            |> List.map (fun (id, ts) -> isoWeek ts, Map.tryFind id kinds |> Option.defaultValue "unknown")
            |> List.countBy id
            |> List.sortBy fst

        let pulledAt id = ev |> List.tryPick (function ts, ItemPulled x when x = id -> Some ts | _ -> None)
        let lead = doneAt |> List.choose (fun (id, ts) -> pulledAt id |> Option.map (fun p -> max 0L (ts - p)))

        // A wait runs from awaiting-human to the next pull or done of that item.
        let waits =
            ev
            |> List.choose (function ts, ItemAwaitingHuman(i, _) -> Some(ts, i) | _ -> None)
            |> List.map (fun (ts, i) ->
                let ended =
                    ev
                    |> List.tryPick (function
                        | t, ItemPulled x | t, ItemDone(x, _, _) when x = i && t >= ts -> Some t
                        | _ -> None)
                ts, ended)
        let closed = waits |> List.choose (fun (s, e) -> e |> Option.map (fun e -> e - s))

        let rechecks = ev |> List.choose (function _, ItemRecheck(_, stale, _) -> Some stale | _ -> None)

        { Throughput = throughput
          LeadTime = timing lead
          HumanWait = timing closed
          OpenWaits = waits |> List.filter (snd >> Option.isNone) |> List.length
          ConflictDeferrals = ev |> List.filter (function _, ConflictDeferred _ -> true | _ -> false) |> List.length
          RecheckRate =
            if rechecks.IsEmpty then None
            else Some(float (rechecks |> List.filter id |> List.length) / float rechecks.Length)
          EscapesByOrigin =
            ev
            |> List.choose (function _, EscapeRecorded(_, o) -> Some(defaultArg o "unclassified") | _ -> None)
            |> List.countBy id
            |> List.sortBy fst }
