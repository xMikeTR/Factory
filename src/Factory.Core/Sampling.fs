namespace Factory.Core

/// A source of uniform random numbers in [0,1). Tests inject a seeded one, so routing stays deterministic.
type Rng = unit -> float

[<RequireQualifiedAccess>]
module Sampling =
    /// A uniform draw in (0,1]: never zero, so it is safe to take a log of.
    let private openUnit (rng: Rng) : float = 1.0 - rng ()

    /// A standard normal deviate by Box-Muller.
    let private normal (rng: Rng) : float =
        let u1 = openUnit rng
        let u2 = rng ()
        sqrt (-2.0 * log u1) * cos (2.0 * System.Math.PI * u2)

    /// A Gamma(shape, 1) draw by Marsaglia-Tsang. For shape < 1 it draws Gamma(shape + 1) and scales by u^(1/shape).
    let rec gamma (rng: Rng) (shape: float) : float =
        if shape < 1.0 then
            gamma rng (shape + 1.0) * (openUnit rng ** (1.0 / shape))
        else
            let d = shape - 1.0 / 3.0
            let c = 1.0 / sqrt (9.0 * d)
            let rec loop () =
                let x = normal rng
                let t = 1.0 + c * x
                let v = t * t * t
                if v <= 0.0 then
                    loop ()
                else
                    let u = openUnit rng
                    if log u < 0.5 * x * x + d - d * v + d * log v then d * v else loop ()
            loop ()

    /// A Beta(a, b) draw as a ratio of two gamma draws.
    let beta (rng: Rng) (a: float) (b: float) : float =
        let x = gamma rng a
        let y = gamma rng b
        if x + y = 0.0 then 0.5 else x / (x + y)
