module Factory.Core.Tests.Support

exception AssertFailed of string

let check cond msg = if not cond then raise (AssertFailed msg)
let equal expected actual msg = if expected <> actual then raise (AssertFailed $"{msg}: expected %A{expected}, got %A{actual}")
