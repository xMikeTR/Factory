// Two writers against Quotes DB. tcNoControl should fail, tcOptimistic should pass.
type tReadResp = (value: int, version: int);
type tWriteReq = (client: machine, value: int, expected: int);

event eRead: machine;
event eReadResp: tReadResp;
event eWrite: tWriteReq;
event eWriteResp: bool;
event eCommitted: int;

machine QuotesDb {
  var value: int;
  var version: int;
  var optimistic: bool;

  start state Init {
    entry (mode: bool) { optimistic = mode; goto Serving; }
  }
  state Serving {
    on eRead do (client: machine) {
      send client, eReadResp, (value = value, version = version);
    }
    on eWrite do (w: tWriteReq) {
      if (optimistic && w.expected != version) {
        send w.client, eWriteResp, false;
        return;
      }
      value = w.value;
      version = version + 1;
      announce eCommitted, value;
      send w.client, eWriteResp, true;
    }
  }
}

machine Writer {
  var db: machine;
  start state Init {
    entry (d: machine) { db = d; goto Working; }
  }
  state Working {
    entry { send db, eRead, this; }
    on eReadResp do (r: tReadResp) {
      send db, eWrite, (client = this, value = r.value + 1, expected = r.version);
    }
    on eWriteResp do (ok: bool) {
      if (ok) { goto Done; }
      goto Working;
    }
  }
  state Done {}
}

spec NoLostUpdate observes eCommitted {
  var commits: int;
  start state Watching {
    on eCommitted do (v: int) {
      commits = commits + 1;
      assert v == commits, format("lost update: {0} commits, value {1}", commits, v);
    }
  }
}

machine NoControl {
  start state Init { entry {
    var db: machine;
    db = new QuotesDb(false);
    new Writer(db); new Writer(db);
  } }
}
machine Optimistic {
  start state Init { entry {
    var db: machine;
    db = new QuotesDb(true);
    new Writer(db); new Writer(db);
  } }
}

test tcNoControl [main = NoControl]: assert NoLostUpdate in { NoControl, QuotesDb, Writer };
test tcOptimistic [main = Optimistic]: assert NoLostUpdate in { Optimistic, QuotesDb, Writer };
