\---

trigger: glob

globs: "\*\*/\*.cs"

description: Strict zero-magic-number, anti-hardcoding, and C# vision code integrity rules enforced on all C# files.

\---



\# C# Hard Constraints: Zero Magic Numbers \& Anti-Evasion Rules



Apply these non-negotiable constraints whenever writing, refactoring, debugging, or reviewing any `.cs` file.



\## 1. Zero Magic Numbers Policy (New \& Existing Code)



\- \*\*Inline Literal Whitelist:\*\* Inside `if`, `for`, `while`, `switch`, return statements, and arithmetic expressions, ONLY `0`, `1`, `-1`, `2`, and `0.5f` may appear as raw number literals.

\- \*\*No Legacy Code Exemption (Kill Minimal-Diff Bias):\*\* When modifying or refactoring existing code, NEVER leave pre-existing bare numbers (such as `< 16`, `31`, `255`, `3.0f`, `1e-6f`) untouched. Actively extract them into named constants, runtime intrinsics, or domain `Spec` properties.

\- \*\*4 Mandatory Replacements for Bare Numbers:\*\*

&#x20; 1. \*\*SIMD, Bitmasks \& Type Limits:\*\* Use `Vector256<T>.Count`, `\~(Vector256<T>.Count - 1)`, `sizeof(T)`, `byte.MaxValue`, `ushort.MaxValue` instead of `8`, `16`, `31`, `32`, `255`, `65535`.

&#x20; 2. \*\*Mathematical \& Standard Coefficients:\*\* Declare `private const` with the formula or standard name + a 1-line `// \[Math Why]:` comment (`GaussianThreeSigmaCutoff = 3.0f`, `SingularDeterminantEpsilon = 1e-6f`).

&#x20; 3. \*\*Algorithm Thresholds \& Counts:\*\* Move minimum sample counts, contrast thresholds, and iteration limits into the domain `readonly record struct` (`Spec`) with `public const` named defaults (`DefaultMinSupportPoints = 16`).

&#x20; 4. \*\*Separate Coincidental Numbers:\*\* If two conditions share the same numeric value for different reasons (`orderedSelection.Count < 16 || denseCandidates.Count < 16`), define separate named constants or `Spec` properties for each role.



\### Contrastive Enforcement Example



```csharp

// ❌ REJECT (Bare magic numbers, raw bitmasks, and ambiguous default return):

if (orderedSelection.Count < 16 || denseCandidates.Count < 16)

{

&#x20;   return default;

}

int alignedWidth = width \& \~31;

int kernelRadius = (int)MathF.Ceiling(sigma \* 3.0f);

if (MathF.Abs(det) < 1e-6f) return default;



// ✅ APPROVED (Self-documenting constants, Spec properties, and intrinsics):

// \[Math Why]: 3-sigma rule captures 99.73% of continuous Gaussian energy.

private const float GaussianThreeSigmaCutoff = 3.0f;



// \[Math Why]: Guard against rank-deficient 2D affine normal matrix inversion.

private const float SingularMatrixDeterminantEpsilon = 1e-6f;

private const int MinAffineFitSampleCount = 16;



if (orderedSelection.Count < spec.MinCoarseCandidates || denseCandidates.Count < MinAffineFitSampleCount)

{

&#x20;   return PoseMatchResult.Empty;

}

int vectorByteCount = Vector256<byte>.Count;

int alignedWidth = width \& \~(vectorByteCount - 1);

int kernelRadius = (int)MathF.Ceiling(sigma \* GaussianThreeSigmaCutoff);

if (MathF.Abs(det) < SingularMatrixDeterminantEpsilon)

{

&#x20;   return PoseMatchResult.Empty;

}

```



\## 2. Zero-Evasion \& Anti-Hardcoding Policy (Anti-Reward-Hacking)



\- \*\*No Input Sniffing or Lookup Cheating:\*\* Never branch on specific test inputs, image dimensions, or coordinates to return pre-calculated expected answers. Every output must be computed dynamically by the general algorithm.

\- \*\*No Empirical Fudge Factors:\*\* Never add arbitrary constants (`+ 0.017f`, `\* 0.985f`) to compensate for coordinate or math bugs.

\- \*\*No Silent Fallbacks or `return default` Traps:\*\*

&#x20; - Never hide failing solvers, SIMD edge cases, or unimplemented paths behind empty `catch` blocks, `// TODO` stubs, or silent `return default;`.

&#x20; - Value objects (`readonly record struct`) must expose an explicit static empty/invalid representation (`EdgeHit.Empty`, `PoseMatchResult.Empty`, or `Result<T>.Failure(...)`) with an `IsEmpty` / `IsValid` property.

\- \*\*Immutable Test Integrity (Never Weaken Tests):\*\*

&#x20; - When a test fails, NEVER widen assertion tolerances (e.g., changing `0.02f` to `0.5f`), delete failing test cases, or modify synthetic ground-truth data.

&#x20; - Diagnose and fix the actual root cause across:

&#x20;   1. Pixel center convention (`0.0` vs `0.5` offset)

&#x20;   2. Kernel weight / derivative normalization ($\\sum w\_i = 1$)

&#x20;   3. Row stride addressing (`y \* stride + x`, never `y \* width + x`)

&#x20;   4. Floating-point catastrophic cancellation or integer accumulator overflow

\- \*\*Multi-Case Verification:\*\* Always verify algorithms with parameterized xUnit `\[Theory]` tests across at least 3 distinct parameter sets (varying sub-pixel shifts, angles, and odd/padded strides) so constant returns cannot pass.



\## 3. Pragmatic LINQ \& Two-Tier Hot-Path Boundary



\- \*\*Tier 1 (Orchestration, Config \& Small Post-Processing):\*\* Clean 1-2 stage LINQ (`Where`, `Select`, `OrderBy`, `MinBy`/`MaxBy`) is permitted when it improves readability. Break 3+ stage LINQ chains into named intermediate variables.

\- \*\*Tier 2 (Per-Pixel, Per-Row, Pyramid \& Frame Hot Paths):\*\* Strictly zero LINQ, zero lambda closures, zero per-frame heap allocations (`0 B`), and explicit `rowOffset = y \* stride` indexing.



\## 4. Mandatory Pre-Output Literal Scan



Before outputting or applying any C# code change, verify:

1\. Are there ANY bare numbers outside `{0, 1, -1, 2, 0.5f}` inside `if`, `for`, `while`, or math expressions (including untouched legacy lines)? If yes, extract them into `const`, intrinsics, or `Spec` properties immediately.

2\. Are there any bare `return default;` statements on domain structs? If yes, replace with `.Empty` or `Result<T>`.

3\. Did any test fail? If yes, fix the mathematical/memory root cause in the production code without touching test tolerances.

