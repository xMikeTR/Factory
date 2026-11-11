workspace "RE Fares Engine" {
  model {
    traveller = person "Traveller" {
      properties {
        "d.zone" "corporate"
      }
    }
    catVendor = softwareSystem "Weather Feed Vendor" {
      tags "External"
      properties {
        "d.aka" "api.weatherfeed-vendor.com"
        "d.zone" "vendor"
        "d.availability" "0.995"
      }
    }
    fares = softwareSystem "Fares Platform" {
      properties {
        "d.zone" "platform"
      }
      portal = container "Fares Portal" "" "React" {
        properties {
          "d.code" "src/Fares.Portal/**"
          "d.risk" "low"
          "d.instances" "2"
          "d.availability" "0.999"
        }
      }
      api = container "Fares API" "" ".NET 8" {
        properties {
          "d.code" "src/Fares.Api/**, tests/Fares.Api.Tests/**"
          "d.risk" "medium"
          "d.instances" "3"
          "d.critical" "yes"
          "d.availability" "0.999"
        }
      }
      surcharge = container "Surcharge Engine" "" "F#" {
        properties {
          "d.code" "src/Surcharge.Engine/**"
          "d.risk" "medium"
          "d.instances" "2"
          "d.critical" "yes"
          "d.availability" "0.999"
        }
      }
      catAdapter = container "Weather Feed Adapter" "" "Python" {
        properties {
          "d.code" "src/WeatherFeed.Adapter/**"
          "d.risk" "medium"
          "d.instances" "1"
          "d.critical" "yes"
          "d.availability" "0.999"
          "d.suppress" "DC005: vendor licence is per-instance; falls back to cached losses (ADR-014)"
        }
      }
      escalations = container "Escalation Worker" "" ".NET 8" {
        properties {
          "d.code" "src/Escalation.Worker/**"
          "d.risk" "medium"
          "d.instances" "2"
        }
      }
      bookings = container "Bookings DB" "" "Azure SQL" "Database" {
        properties {
          "d.code" "src/Fares.Data/**, db/migrations/**, tests/Fares.Data.Tests/**"
          "d.risk" "high"
        }
      }
    }

    traveller -> portal "Prices a risk" "HTTPS" {
      properties {
        "d.interaction" "sync"
        "d.encrypted" "yes"
        "d.authenticated" "yes"
      }
    }
    portal -> api "Requests booking" "HTTPS/JSON" {
      properties {
        "d.interaction" "sync"
      }
    }
    api -> surcharge "Rates layer" "gRPC" {
      properties {
        "d.interaction" "sync"
        "d.retry" "fixed"
        "d.retry.max" "3"
        "d.idempotent" "yes"
      }
    }
    api -> bookings "Saves booking" "EF Core" {
      properties {
        "d.interaction" "sync"
        "d.writes" "Booking"
        "d.concurrency" "optimistic"
      }
    }
    api -> escalations "Sends escalation" "Azure Service Bus" {
      properties {
        "d.interaction" "async"
        "d.delivery" "at-least-once"
        "d.idempotent" "yes"
      }
    }
    surcharge -> catAdapter "Requests modelled loss" "gRPC" {
      properties {
        "d.interaction" "sync"
        "d.idempotent" "yes"
      }
    }
    catAdapter -> catVendor "Submits weather feed run" "HTTPS" {
      properties {
        "d.interaction" "async"
        "d.delivery" "at-least-once"
        "d.encrypted" "yes"
        "d.authenticated" "yes"
        "d.retry" "backoff"
        "d.retry.max" "1"
        "d.idempotent" "yes"
      }
    }
    escalations -> bookings "Updates booking status" "Dapper" {
      properties {
        "d.interaction" "sync"
        "d.writes" "Booking"
        "d.concurrency" "optimistic"
      }
    }
  }
}
