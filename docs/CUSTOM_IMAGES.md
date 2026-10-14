# Custom image rugs

Choose **Custom image > Choose a PNG or JPEG...** from the tray or rug menu. The image is fitted uniformly inside the default
rug area; unused space and PNG holes stay transparent. The same picture is used on the front, on the back (mirrored by the
fold), and on the roll. While an image is selected, **Cloth feel** changes only the physics preset, not its size or lighting.

**Shade the folds** is off by default. Off uses the decoded image colors without fabric effects. On adds geometric RGB
lighting to folds and the roll, leaving surface alpha unchanged. The opaque rug height-map shadow, bump overlay, fringe,
backing, weave, Aladdin, dust and straps are not applied to custom images. This deliberately avoids filling transparent
regions with decoration or shadow. Built-in styles retain their existing appearance and settings, including Aladdin.
The merchant, sorting, sweeping, icon handling, rolling and original settings remain available.

**Clear image: use built-in rug** deletes only the app-owned image copy and returns to the selected built-in style. The
image lighting preference is kept for the next image. Image changes are rejected while any monitor is being dragged,
flattened, rolled, or has a pending/active desktop or merchant job, including the merchant walking away. This keeps a
window rebuild from finishing or aborting a real desktop job. The state is checked again after the file picker closes.

Before clearing an active image, the app renders the selected built-in style at every monitor's target size. If that
rendering fails, it keeps the image copy and existing windows, with advice to choose another cloth feel before clearing.
Clearing a corrupt stored copy with no active image does not rebuild the already displayed built-in rugs. This check
handles predictable art failures; it does not provide rollback for later window creation failures or memory exhaustion.

## Files and limits

- Local PNG/JPEG files only, checked by extension and decoder contents. Cancellation does nothing.
- At most 20 MiB encoded, 8192 pixels per side, 32 megapixels, and an aspect ratio no more extreme than 256:1.
- The display cache is limited to 2048 pixels on its longest side. Its DPI ratios retain the exact oriented source aspect
  when rounding the smaller pixel side. Drawing and 8-bit color conversion may introduce one-level rounding differences;
  this is not a promise of source-resolution pixels at desktop scale.
- Embedded profiles are converted for display to sRGB; JPEG EXIF orientations 1 through 8 are honored. The stored copy
  retains the original encoded bytes, including metadata, rather than re-encoding premultiplied pixels at each import.
- The source is read and closed before saving `%APPDATA%\AladdinRug\image\texture.image`. The source path is never stored,
  and deleting or changing the source later does not change the rug. The app copy is a PNG/JPEG despite its private extension.
- Image and lighting replacements use a unique temporary file, flush, then atomic replacement. On failure the previous
  file and in-memory value remain. Clearing removes only the managed image. No source file is renamed, deleted or rewritten.
- A missing copy falls back to the built-in style. A corrupt saved image/settings file is left intact and reported;
  selecting another image or clearing it is explicit. Directory/file links at the managed destination are rejected.

## Implementation boundary

This change builds on upstream main `4d478c219fe5d763c77a52f65bf4ef897bc072b1`, which includes the cloth self-collision
fix from PR #1. Its continuous collision checks, triangle-preserving subdivision, snapshot locks and flatten pause are
retained. See [Cloth self-collision](CLOTH_COLLISION.md) for the solver's scope and limits. The image path adds selection
of a visible material node; built-in rugs keep the existing geometric selection. Scripted flatten remains unchanged.

Compared with the earlier image-only handoff based on `5b40cabe`, folds follow the physical triangles instead of
Catmull-Rom curves. This can change folded silhouettes and shading; it is the already-merged collision fix, not a new
image effect. Flat image colors, transparency and the image import/storage behavior are unchanged by integration.

The renderer keeps its original path for built-in rugs. Images use reusable scan bands of fragments sorted by each pixel's
interpolated depth, then premultiplied source-over composition. Their hit map selects the visible surface, including a lower
layer exposed through a hole; it is copied on the UI thread with the exact picture uploaded. Only this image path provides
the optional material-node argument to Grab. Once a drag is captured it can continue across transparent pixels.

The app identity, MIT license, AppData migration, IPC, startup handling and desktop jobs are unchanged. In particular this
is not the separate physics-only personal app: normal startup still contains the original migrations and desktop features.

## Image-only baseline validation, 2026-10-13

The following measurements describe the earlier image-only handoff, before integration with PR #1. They are retained
as historical evidence and are not new measurements of the combined candidate.

The Release application builds with zero warnings/errors. The 20 focused checks in `Dev/ImageChecks.cs` pass in a
source-linked executable that excludes Program, AppFolders, RugWindow and desktop jobs. They cover source hash/lock,
restart after source deletion, plain JPEG and eight EXIF orientations, embedded sRGB, cancellation, corrupt/disguised/missing
files, limits/downsampling, atomic replacement/clear/lighting failures, recovery, flat/front/back/roll pixels, alpha boundary
values, aspect, depth composition, transparent holes, displayed hit maps and captured/stale-frame grabbing.

Additional offscreen checks load the compiled assembly without invoking Program, RugSet or RugWindow constructors:
25 blocked states across two monitors reject import, clear and lighting before any write/rebuild. Five roll stages preserve
translucent alpha without counting the clipped flat image twice; built-in shadow/straps remain enabled.

Eleven restore checks source-link the real RugSet, TextureStore and art with pure style definitions and inert window/desktop
services. Failure on either monitor preserves the managed file hash, Current reference, window list and quitting state;
four styles with Aladdin on/off restore at a working scale, and corrupt-copy-only clearing skips the rebuild. These checks
stay separate from ImageChecks so its synthetic-image path does not initialize the real style settings or AppData folders.

An isolated serial comparison matches all 42 hashes: four styles with Aladdin on/off, face/back/flat/fold pixels and physics
positions, plus repeated Doormat renders. The first comparison had five different Doormat hashes out of 40; the cause was
not established. The later serial comparison, including within-process repeats, matches exactly; the earlier result is
retained in local evidence rather than presented as an unconditional deterministic-output guarantee.

An existing upstream issue is reproduced unchanged in both versions: Shaggy can throw `IndexOutOfRangeException` in
`RugArt.Noise.At`, reached by `ShagWeave` sampling at `(x + 300, y + 200)` beyond the allocated noise grid. Forty-four
conditions per version give the same success/failure results, including 600 x 420 at scale 1 and the tested monitors'
768 x 529 and 768 x 474 at scale 1. Style pixel comparison uses scale 1.5, where all four render. Clearing an image into
an affected Shaggy size is now rejected before deleting the image or replacing its windows. The user can select another
cloth feel and clear again. No noise or style redesign is included, and successful Shaggy restore at every scale is not claimed.

### Rendering measurements

Measured 2026-10-12: Windows 11, Intel Core Ultra 7 258V, .NET SDK 10.0.401, Release x64. Three synthetic overlapping strips, 8 warmup and
60 measured frames. Values below cover CPU rasterization only, not physics, window composition, upload, or pointer latency.
Live memory is the harness's retained renderer/art/output footprint, not the complete app or all monitors.

| Output | Mode | p50 ms | p95 ms | Live MiB |
| --- | --- | ---: | ---: | ---: |
| 1920 x 1080 | builtin | 9.52 | 12.53 | 20.1 |
| 1920 x 1080 | opaque image | 6.92 | 9.02 | 36.8 |
| 1920 x 1080 | alpha image | 4.67 | 7.31 | 30.9 |
| 1920 x 1080 | alpha image shaded | 5.08 | 6.96 | 30.9 |
| 3840 x 2160 | builtin | 14.77 | 19.08 | 81.0 |
| 3840 x 2160 | opaque image | 13.08 | 16.28 | 151.7 |
| 3840 x 2160 | alpha image | 15.29 | 17.43 | 126.1 |
| 3840 x 2160 | alpha image shaded | 15.63 | 19.55 | 126.1 |

Baseline built-in rendering was 10.00/18.18 ms (p50/p95) at 1920 x 1080 and 13.60/18.78 ms at 3840 x 2160. These short runs
show variability and are not a precise performance-regression threshold. The 4K alpha modes exceed a 16.7 ms frame budget
at p95 before physics/upload; no 60 FPS claim is made. Fragment storage grows with visible overlap, so highly piled cloth
and multiple monitors can use more memory than this fixture. No GPU rewrite is included.

### Not run and protected-state boundary

The automated baseline work did not run normal GUI startup, installation, auto-start changes, the original `--selftest`,
Desktop sample scripts or desktop file/icon jobs. The image-only handoff subsequently received manual GUI acceptance.
That acceptance is preserved separately from automated checks and does not establish a GUI run of the combined candidate.
Pixel checks and offscreen controls do not establish mouse, picker, tray, multiple-DPI or merchant-animation behavior.
The existing --preview path does not accept a custom image and initializes real settings/AppData and a hidden window;
it is not a substitute for image GUI validation.

Before/after local validation records cover user/Public Desktop, the existing app/legacy AppData directories, both Run keys
and the Explorer Desktop layout registry key. The existing personal and PR #1 checkouts are hashed separately. The local
evidence contains the comparison and exact final source/build hashes.

If a future GUI check is needed, identify the exact binary and use a disposable Windows account or VM with a synthetic Desktop and
separate AppData. Capture the above paths and keys, exercise image choose/cancel/clear, restart after deleting only the
synthetic source, all four feels, fold/roll/alpha interactions, and pending/active merchant guards; compare snapshots again.
The original app's startup/migration and desktop operations must not be used on the user's real Desktop during this test.
