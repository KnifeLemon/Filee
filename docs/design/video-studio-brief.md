# Frame Video Studio surface brief

**Mode:** Operate
**Route:** `docs/design/video-studio.html`
**Audience:** A creator preparing a text-to-video request.
**Primary outcome:** Submit a scene brief and requested output settings to an explicitly configured provider, then see provider-reported status and returned media.

## Surface requirements

- Prompt is required; one reference image is optional.
- Resolution includes 720p, 1080p and 4K (2160p); picture quality and visual direction are explicit request options.
- Users can choose aspect ratio and requested clip duration.
- With no provider configured, the page remains useful for composing a brief but reports generation as unavailable and does not transmit form data.
- Status, progress, preview and download are shown only when backed by the provider response.

## Provider API contract

Set the `video-api-base` meta tag in `video-studio.html` to the provider base URL. The provider must allow browser requests from the page origin with CORS. This demo stores no provider secrets; use an appropriate authenticated integration or same-origin server proxy for private credentials.

1. `POST {base}/generations` as `multipart/form-data`: `prompt` (text), `parameters` (JSON containing `aspect_ratio`, `duration_seconds`, `resolution`, `quality`, `visual_style`), and optional `reference_image` (image file).
2. Response: JSON with string `job_id`.
3. `GET {base}/generations/{job_id}`: JSON `status` of `queued`, `running`, `completed`, or `failed`. Running responses may include numeric `progress` (0–100). Completed responses may include `preview_url`, `download_url` and `download_filename`. Failed responses may include `error`.

The page does not include credentials, select a provider, proxy traffic, or guarantee every provider supports each requested option. Provider errors are surfaced to the user. No video-generation backend or credentials are included in this repository.
