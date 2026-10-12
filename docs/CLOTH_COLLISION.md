# Cloth self-collision

Dragging a corner diagonally across the rug can move an edge through a distant triangle. The old stacking pass
only separated nearby points vertically; it did not cover edges, triangle interiors, or a crossing between steps.
Catmull-Rom drawing could also leave the simulated surface, and drawing could read a mixture of position snapshots.

The drag simulation now projects triangle contacts and checks the complete proposed substep with conservative
advancement. Each check includes the springs, contact corrections and floor/wall constraints. If a pair cannot
finish its motion safely, the whole substep accepts only the safe prefix, including the hand and its lift. Actual
accepted movement drives the existing sleep threshold. Floor friction still runs once per substep.

`ClothCollision` uses the same two triangles per cell as the drawing mesh. It excludes triangles sharing a vertex,
checks all vertex/face and edge/edge features, and uses a 0.1 DIP advancement guard. An existing near-contact may
move when a fixed separating axis proves positive separation along the whole path. The bounded advancement loop stops
at its proven prefix if it exhausts its budget. A grid finds candidate pairs, with a refitted tree for large spans;
small meshes reuse candidates only while their saved bounds contain every fresh bound. Contact corrections run
in a stable sequence; parallel workers only gather candidates or compute independent motion limits.

Drawing subdivides the physical triangles without moving their surface. This trades some of the old curved
silhouette for consistency with collision. Position snapshots publish all three axes under the drawing lock.
The flatten pause is checked again after taking the physics lock, so an already-awake worker cannot step into it.

## Regression checks

```bash
AladdinRug.exe --collisiontest report.txt
```

Run without `--style`. These checks use synthetic geometry and the default cloth feel, write only the requested
report, and do not create a window or use desktop/settings services. Invalid argument counts return 2, failures
return 1, and success returns 0. The 12 cases cover vertex/face and edge/edge motion, moving faces with safe endpoints,
coplanar/degenerate triangles, shared translation, sliding away from contact, held surfaces, exact drawing geometry,
fold/release/re-grab, room boundaries, large-mesh tracking and the existing final flattened layout.

Local validation on Windows 11, .NET SDK 10.0.401, Intel Core Ultra 7 258V (2026-10-12):

- Release solution build: no warnings or errors.
- All 12 checks passed through a separate source-linked host that excluded app startup, window and shell classes.
- Two additional headless thread checks passed: an in-flight physics worker respects flatten pause, and drawing's
  snapshot lock prevents partial-axis publication.
- Four actual style feels were tested with the original two-argument `Grab`: a 708 × 470 DIP rug at (200, 160),
  20 DIP target spacing, grabbing (908, 160), dragging (-500, +330) over 80 frames, releasing at frame 140,
  and continuing through frame 219 at 1/60 second per step. A separate segment/plane/barycentric oracle detected
  the first strict surface crossing at frames 20/19/20/20 on main `5b40cabe` (Persian/Kilim/Shaggy/Doormat),
  and none on this patch. All four moved 500 DIP with zero hand lag at frame 100.
- In a deliberately uneven mesh, the old drawing had 57 coordinate mismatches from the physical triangles
  (maximum 17.62 DIP); the patched drawing had none.

## Cost and limits

The same source-linked harness compared main `5b40cabe` with this patch, using the same diagonal input on each mesh.
Each result is from three runs, each warming 30 frames and timing the next 150 (450 samples). The grid is 20 DIP;
the default mesh is 708 × 470 DIP, and the 4K-sized mesh is 1476 × 934 DIP. Values are physics-step p50 / p95:

| Mesh | Original main | This patch |
| --- | --- | --- |
| default (900 points) | 2.59 / 3.23 ms | 8.91 / 12.50 ms |
| 4K (3,600 points) | 10.74 / 11.91 ms | 33.63 / 48.80 ms |

Continuous surface collision costs more than the old point stacking. The 4K case does not meet a 16.7 ms physics
budget on this laptop; these measurements are not an end-to-end frame-rate claim. Full GUI timing and visual
acceptance remain unverified. No real desktop organization, sweeping, startup migration, registry changes,
or installation was run for these checks.

The existing scripted flatten-before-roll/merchant transition is unchanged and may pass through layers. It blocks
dragging and discards the cloth at completion; this patch's collision guarantee covers the physics steps used for
dragging and releasing, not that scripted transition. Roll, merchant, tidy/sweep, icons, settings, character art,
and the application identity keep their original code. No image feature or separate test framework is added.
