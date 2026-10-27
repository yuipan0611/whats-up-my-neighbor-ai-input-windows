# Desktop companion artwork

Approved YUI Neko C mascot, exported from the existing Figma design:
https://www.figma.com/design/9GCLMgCKB0DnkDUDCrN7eN?node-id=3-39

- `sit.svg`: Sit / Idle, node 3:16
- `walk.svg`: Walk / Idle, node 3:25
- `reply.svg`: Sit / Reply, node 3:37
- `body.svg`: sitting body, node 3:8
- `tail.svg`: sitting tail, node 3:7
- `walk-body.svg`: unchanged tail/body paths extracted from `walk.svg`
- `walk-paws.svg`: unchanged four-paw path extracted from `walk.svg`
- `walk-head.svg`: unchanged round head outline extracted from `walk.svg`

SVG exports are kept unchanged. The native view composes the original parts for loafing/sleeping and adds small native expression marks. Black fur remains #101114. All artwork is bundled locally; no temporary Figma asset URLs are used at runtime. Custom cat.config.json PNGs retain their existing recording/greeting behavior.

Locomotion composes the original body, diagonal paw groups and narrowed round head facing travel. The head keeps the approved outline, without adding a protruding muzzle. Only the walking silhouette reflects; its X/check glyphs retain their handedness. The original omega mouth remains below the eyes. Front/rest poses always use the original orientation, regardless of the previous walk direction.

Paw stance follows the model's actual horizontal distance, including acceleration: planted paws stay fixed on the desktop, while each diagonal pair takes its turn lifting and swinging forward. This does not increase the existing 12 fps walking limit.

The approved paw-licking pose uses groom-head.svg / groom-body.svg extracted
from the original sit.svg, plus native palm and short rounded tongue drawing.
walk-foot-0.svg through walk-foot-3.svg retain the exact original paw contours;
broad roots connect them to the torso and neutral startup avoids a first-step pop.
