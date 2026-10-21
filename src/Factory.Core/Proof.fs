namespace Factory.Core.Gate

type CheckResult =
    { Name: string
      Passed: bool
      Detail: string
      Artifacts: System.Uri list }

type Approval = { By: string; Source: string }

type ProofBundle =
    { Subject: string
      Commit: string
      Checks: CheckResult list
      Approvals: Approval list }

type Proof = private Proof of ProofBundle

[<RequireQualifiedAccess>]
module Proof =
    let tryCreate (required: string list) (bundle: ProofBundle) : Result<Proof, string list> =
        let problems =
            [ for name in required do
                  match bundle.Checks |> List.filter (fun c -> c.Name = name) with
                  | [] -> $"required check '{name}' is missing"
                  | cs when cs |> List.forall (fun c -> c.Passed) -> ()
                  | cs -> $"required check '{name}' failed: {(cs |> List.find (fun c -> not c.Passed)).Detail}"
              if bundle.Approvals.IsEmpty then "no approval" ]
        if problems.IsEmpty then Ok(Proof bundle) else Error problems

    let value (Proof b) = b
    let covers subject commit (Proof b) = b.Subject = subject && b.Commit = commit

type Outcome =
    | Done of Proof
    | Blocked of reasons: string list
    | NeedsHuman of question: string
