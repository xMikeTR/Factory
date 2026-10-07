workspace "RE Pricing Engine" {
  model {
    underwriter = person "Underwriter" {
      properties {
        "d.zone" "corporate"
      }
    }
    catVendor = softwareSystem "Cat Model Vendor" {
      tags "External"
      properties {
        "d.aka" "api.catmodel-vendor.com"
        "d.zone" "vendor"
        "d.availability" "0.995"
      }
    }
    pricing = softwareSystem "Pricing Platform" {
      properties {
        "d.zone" "platform"
      }
      portal = container "Pricing Portal" "" "React" {
        properties {
          "d.code" "src/Pricing.Portal/**"
          "d.risk" "low"
          "d.instances" "2"
          "d.availability" "0.999"
        }
      }
      api = container "Pricing API" "" ".NET 8" {
        properties {
          "d.code" "src/Pricing.Api/**, tests/Pricing.Api.Tests/**"
          "d.risk" "medium"
          "d.instances" "3"
          "d.critical" "yes"
          "d.availability" "0.999"
        }
      }
      rating = container "Rating Engine" "" "F#" {
        properties {
          "d.code" "src/Rating.Engine/**"
          "d.risk" "medium"
          "d.instances" "2"
          "d.critical" "yes"
          "d.availability" "0.999"
        }
      }
      catAdapter = container "Cat Model Adapter" "" "Python" {
        properties {
          "d.code" "src/CatModel.Adapter/**"
          "d.risk" "medium"
          "d.instances" "1"
          "d.critical" "yes"
          "d.availability" "0.999"
          "d.suppress" "DC005: vendor licence is per-instance; falls back to cached losses (ADR-014)"
        }
      }
      referrals = container "Referral Worker" "" ".NET 8" {
        properties {
          "d.code" "src/Referral.Worker/**"
          "d.risk" "medium"
          "d.instances" "2"
        }
      }
      quotes = container "Quotes DB" "" "Azure SQL" "Database" {
        properties {
          "d.code" "src/Pricing.Data/**, db/migrations/**, tests/Pricing.Data.Tests/**"
          "d.risk" "high"
        }
      }
    }

    underwriter -> portal "Prices a risk" "HTTPS" {
      properties {
        "d.interaction" "sync"
        "d.encrypted" "yes"
        "d.authenticated" "yes"
      }
    }
    portal -> api "Requests quote" "HTTPS/JSON" {
      properties {
        "d.interaction" "sync"
      }
    }
    api -> rating "Rates layer" "gRPC" {
      properties {
        "d.interaction" "sync"
        "d.retry" "fixed"
        "d.retry.max" "3"
        "d.idempotent" "yes"
      }
    }
    api -> quotes "Saves quote" "EF Core" {
      properties {
        "d.interaction" "sync"
        "d.writes" "Quote"
        "d.concurrency" "optimistic"
      }
    }
    api -> referrals "Sends referral" "Azure Service Bus" {
      properties {
        "d.interaction" "async"
        "d.delivery" "at-least-once"
        "d.idempotent" "yes"
      }
    }
    rating -> catAdapter "Requests modelled loss" "gRPC" {
      properties {
        "d.interaction" "sync"
        "d.idempotent" "yes"
      }
    }
    catAdapter -> catVendor "Submits cat model run" "HTTPS" {
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
    referrals -> quotes "Updates quote status" "Dapper" {
      properties {
        "d.interaction" "sync"
        "d.writes" "Quote"
        "d.concurrency" "optimistic"
      }
    }
  }
}
