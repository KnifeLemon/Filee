# Frame Video Studio design system

## Surface

Frame is a standalone AI video-studio concept at `video-studio.html`. This is an **Operate** surface: success means a creator can describe a scene, set requested output, attach an optional visual reference, and understand whether a real provider can act. It is not part of Filee's offline file-conversion product.

## Visual direction

An editorial darkroom: deep ink surfaces keep playback central, restrained violet light adds depth, and a single acid-lime accent marks actions and active states. The studio should feel focused and tactile, not like a generic chat window or marketing landing page. The preview illustration is explicitly labeled as a studio canvas and never presented as generated work.

## Tokens

| Token | Value | Use |
|---|---|---|
| Page | `#111014` | Ambient page background |
| Surface | `#19171C` | Primary panels |
| Raised surface | `#201E24` | Raised controls |
| Line | `#333038` | Panel and control boundaries |
| Text | `#F2EFF5` | Primary text |
| Muted | `#A19CA8` | Supporting copy |
| Accent | `#D6FF69` | Primary action, focus, active output |
| Warning / error | `#F1C47A` / `#FF938B` | Provider activity and errors |

Operating-system sans-serif typography, semantically grouped panels, compact labels and a visible focus ring support a focused workflow without external dependencies. Desktop pairs brief and preview; the responsive layout stacks them on narrow screens with preview first.

## Accessibility and behavior

Semantic landmarks, a skip link, explicit form labels, required prompt validation, a live character count, keyboard-operable image upload, visible focus, polite provider status and `prefers-reduced-motion` support are required. Never invent progress or successful output. Only expose preview and download controls for URLs returned by the provider.

## Provider boundary

`video-studio.html` accepts an optional API base URL from the `video-api-base` meta tag. Endpoint request/response contract and integration notes follow in `video-studio-brief.md`. Without that configuration, submission explains generation is unavailable and confirms no form data was sent. No video-generation backend or credentials are included in this repository.
