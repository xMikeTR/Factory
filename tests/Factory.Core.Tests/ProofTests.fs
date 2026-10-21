module Factory.Core.Tests.ProofTests

open Factory.Core.Gate
open Factory.Core.Tests.Support

let private pass name = { Name = name; Passed = true; Detail = "ok"; Artifacts = [] }
let private mike = { By = "mike"; Source = "ci" }

let private bundle checks approvals =
    { Subject = "T-1"; Commit = "abc123"; Checks = checks; Approvals = approvals }

let private required = [ "acs"; "mutation" ]

let tests: (string * (unit -> unit)) list =
    [ "proof: a missing required check is an error naming it", fun () ->
          match Proof.tryCreate required (bundle [ pass "acs" ] [ mike ]) with
          | Error reasons -> check (reasons |> List.exists (fun r -> r.Contains "mutation")) $"names the missing check: %A{reasons}"
          | Ok _ -> failwith "expected Error"
      "proof: a failed check is an error naming it", fun () ->
          let failed = { pass "mutation" with Passed = false; Detail = "score 40" }
          match Proof.tryCreate required (bundle [ pass "acs"; failed ] [ mike ]) with
          | Error reasons -> check (reasons |> List.exists (fun r -> r.Contains "mutation")) $"names the failed check: %A{reasons}"
          | Ok _ -> failwith "expected Error"
      "proof: no approval is an error", fun () ->
          check (Proof.tryCreate required (bundle [ pass "acs"; pass "mutation" ] []) |> Result.isError) "approval required"
      "proof: every required check passed plus an approval is Ok, and keeps the bundle", fun () ->
          let b = bundle [ pass "acs"; pass "mutation" ] [ mike ]
          match Proof.tryCreate required b with
          | Ok p -> equal b (Proof.value p) "bundle"
          | Error e -> failwith $"expected Ok: %A{e}"
      "proof: all problems are reported together", fun () ->
          match Proof.tryCreate required (bundle [] []) with
          | Error reasons -> equal 3 reasons.Length "two missing checks and no approval"
          | Ok _ -> failwith "expected Error"
      "proof: an extra non-required failing check does not block", fun () ->
          let extra = { pass "lint" with Passed = false }
          check (Proof.tryCreate required (bundle [ pass "acs"; pass "mutation"; extra ] [ mike ]) |> Result.isOk) "extra is advisory"
      "proof: matches only its own subject and commit", fun () ->
          let p = Proof.tryCreate [] (bundle [] [ mike ]) |> Result.defaultWith (fun e -> failwith $"%A{e}")
          check (Proof.covers "T-1" "abc123" p) "same"
          check (not (Proof.covers "T-1" "other" p)) "other commit"
          check (not (Proof.covers "T-2" "abc123" p)) "other subject" ]
