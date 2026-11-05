const $ = (selector) => document.querySelector(selector);
const form = $("#generation-form");
const prompt = $("#prompt");
const imageInput = $("#reference-image");
const uploadZone = $("#upload-zone");
let imageUrl = "";
let pollTimer = 0;
const apiBase = $('meta[name="video-api-base"]').content.trim();

class ProviderError extends Error {}

function setNotice(text, state = "") {
  $("#provider-notice").textContent = text;
  $("#provider-notice").dataset.state = state;
}

function setStatus(state, title, message, progress = null) {
  const status = $("#job-status");
  status.dataset.state = state;
  $("#status-title").textContent = title;
  $("#status-message").textContent = message;
  const hasProgress = Number.isFinite(progress);
  $("#progress-track").hidden = !hasProgress;
  $("#status-percent").textContent = hasProgress ? `${Math.round(progress)}%` : "";
  if (hasProgress) $("#progress-fill").style.width = `${Math.max(0, Math.min(100, progress))}%`;
}

function setReference(file) {
  if (!file) return;
  if (!file.type.startsWith("image/")) {
    setNotice("Choose an image file to use as a reference.", "error");
    imageInput.value = "";
    return;
  }
  if (file.size > 15 * 1024 * 1024) {
    setNotice("This image is larger than 15 MB. Choose a smaller file.", "error");
    imageInput.value = "";
    return;
  }
  if (imageUrl) URL.revokeObjectURL(imageUrl);
  imageUrl = URL.createObjectURL(file);
  $("#reference-thumbnail").src = imageUrl;
  $("#reference-name").textContent = file.name;
  $("#reference-file").hidden = false;
  uploadZone.hidden = true;
  setNotice("Reference image selected. It stays in this browser until you submit a request.");
}

function clearReference() {
  imageInput.value = "";
  $("#reference-thumbnail").removeAttribute("src");
  $("#reference-file").hidden = true;
  uploadZone.hidden = false;
  if (imageUrl) URL.revokeObjectURL(imageUrl);
  imageUrl = "";
}

function updateOutputSummary() {
  const ratio = $("#aspect-ratio").value;
  $("#preview-screen").dataset.ratio = ratio;
  $("#ratio-label").textContent = ratio;
  $("#resolution-label").textContent = $("#resolution").selectedOptions[0].textContent.split(" · ")[0];
  $("#duration-label").textContent = `${$("#duration").value} sec`;
}

function endpoint(path) {
  const url = new URL(apiBase, window.location.href);
  if (!["http:", "https:"].includes(url.protocol)) throw new ProviderError("Provider URL must use HTTP or HTTPS.");
  url.pathname = `${url.pathname.replace(/\/+$/, "")}${path}`;
  url.search = "";
  url.hash = "";
  return url;
}

async function request(path, options = {}) {
  let response;
  try {
    response = await fetch(endpoint(path), {
      ...options,
      headers: { Accept: "application/json", ...options.headers },
    });
  } catch (error) {
    if (error instanceof ProviderError) throw error;
    throw new ProviderError("Could not reach the video provider. Check its URL and CORS configuration.");
  }
  if (!response.ok) {
    const body = await response.json().catch(() => null);
    throw new ProviderError(typeof body?.error === "string" ? body.error : `Provider returned HTTP ${response.status}.`);
  }
  return response.json();
}

function safeMediaUrl(value) {
  if (typeof value !== "string" || !value) return "";
  try {
    const url = new URL(value, endpoint("/"));
    return ["http:", "https:"].includes(url.protocol) ? url.href : "";
  } catch {
    return "";
  }
}

function clearResult() {
  const video = $("#result-video");
  video.pause();
  video.removeAttribute("src");
  video.load();
  video.hidden = true;
  $("#result-image").removeAttribute("src");
  $("#result-image").hidden = true;
  $("#preview-empty").hidden = false;
  $("#preview-empty .empty-title").textContent = "A little space for what’s next.";
  $("#preview-empty .empty-copy").textContent = "When a provider is connected, your video preview will appear here.";
  $("#preview-empty .empty-label").textContent = "STUDIO CANVAS · NO VIDEO GENERATED";
  $("#download").hidden = true;
}

function showResult(job) {
  const previewUrl = safeMediaUrl(job.preview_url);
  const downloadUrl = safeMediaUrl(job.download_url);
  if (previewUrl) {
    $("#preview-empty").hidden = true;
    if (/\.(avif|gif|jpe?g|png|webp)(?:$|[?#])/i.test(previewUrl)) {
      $("#result-image").src = previewUrl;
      $("#result-image").hidden = false;
    } else {
      const video = $("#result-video");
      video.src = previewUrl;
      video.hidden = false;
      video.load();
    }
  } else {
    $("#preview-empty .empty-title").textContent = "Render completed";
    $("#preview-empty .empty-copy").textContent = downloadUrl
      ? "The provider did not return a preview. Download the completed render below."
      : "The provider completed this request but returned no preview or download URL.";
    $("#preview-empty .empty-label").textContent = downloadUrl
      ? "PREVIEW NOT PROVIDED"
      : "NO MEDIA URL RETURNED";
  }
  if (downloadUrl) {
    const link = $("#download");
    link.href = downloadUrl;
    link.hidden = false;
    if (typeof job.download_filename === "string" && job.download_filename.trim()) {
      link.download = job.download_filename.trim();
    }
  }
}

async function pollJob(jobId) {
  try {
    const job = await request(`/generations/${encodeURIComponent(jobId)}`);
    if (job.status === "queued" || job.status === "running") {
      const progress = Number.isFinite(job.progress) ? job.progress : null;
      setStatus(job.status, job.status === "queued" ? "In the provider queue" : "Video in progress", "Status is reported by the connected provider.", progress);
      pollTimer = window.setTimeout(() => pollJob(jobId), 2500);
      return;
    }
    if (job.status === "completed") {
      showResult(job);
      setStatus("completed", "Provider reported completion", job.preview_url ? "Your provider returned a preview." : "The provider completed this request but returned no preview URL.", 100);
      setNotice("The connected provider completed your request.", "success");
      $("#generate").disabled = false;
      return;
    }
    if (job.status === "failed") throw new ProviderError(typeof job.error === "string" ? job.error : "The provider reported that this request failed.");
    throw new ProviderError("The provider returned an unsupported job status.");
  } catch (error) {
    setStatus("failed", "Could not retrieve video status", error.message);
    setNotice(error.message, "error");
    $("#generate").disabled = false;
  }
}

prompt.addEventListener("input", () => {
  $("#prompt-count").textContent = `${prompt.value.length.toLocaleString()} / 2,000`;
});

document.querySelectorAll(".suggestion").forEach((button) => {
  button.addEventListener("click", () => {
    prompt.value = button.dataset.prompt ?? "";
    prompt.dispatchEvent(new Event("input", { bubbles: true }));
    prompt.focus();
  });
});

imageInput.addEventListener("change", () => setReference(imageInput.files?.[0]));
$("#remove-reference").addEventListener("click", clearReference);
uploadZone.addEventListener("keydown", (event) => {
  if (event.key === "Enter" || event.key === " ") {
    event.preventDefault();
    imageInput.click();
  }
});
for (const name of ["dragenter", "dragover"]) {
  uploadZone.addEventListener(name, (event) => {
    event.preventDefault();
    uploadZone.classList.add("drag-over");
  });
}
for (const name of ["dragleave", "drop"]) {
  uploadZone.addEventListener(name, (event) => {
    event.preventDefault();
    uploadZone.classList.remove("drag-over");
  });
}
uploadZone.addEventListener("drop", (event) => setReference(event.dataTransfer?.files?.[0]));
document.querySelectorAll("#aspect-ratio, #resolution, #duration").forEach((control) => control.addEventListener("change", updateOutputSummary));

form.addEventListener("submit", async (event) => {
  event.preventDefault();
  window.clearTimeout(pollTimer);
  clearResult();
  if (!form.reportValidity()) return;
  if (!apiBase) {
    setStatus("unavailable", "Provider connection required", "No video service is configured. Your prompt and image have not been sent anywhere.");
    setNotice("Generation is unavailable until a video provider is connected. Your brief and reference image remain in this browser.", "error");
    return;
  }

  const values = Object.fromEntries(new FormData(form).entries());
  const referenceImage = values.reference_image;
  delete values.prompt;
  delete values.reference_image;
  const body = new FormData();
  body.set("prompt", prompt.value.trim());
  body.set("parameters", JSON.stringify(values));
  if (referenceImage instanceof File && referenceImage.size > 0) body.set("reference_image", referenceImage);
  $("#generate").disabled = true;
  setStatus("queued", "Sending request to provider", "Waiting for the connected provider to accept this brief.");
  setNotice("Submitting your request to the configured provider…");

  try {
    const job = await request("/generations", { method: "POST", body });
    if (typeof job.job_id !== "string" || !job.job_id.trim()) throw new ProviderError("The provider response did not include a job_id.");
    $("#connection").dataset.state = "busy";
    setStatus("queued", "In the provider queue", "The provider accepted this request.");
    setNotice("Your request was accepted by the connected provider.", "success");
    await pollJob(job.job_id);
  } catch (error) {
    setStatus("failed", "Request was not completed", error.message);
    setNotice(error.message, "error");
    $("#generate").disabled = false;
  } finally {
    if (!$("#generate").disabled) $("#connection").dataset.state = "online";
  }
});

if (apiBase) {
  $("#connection").dataset.state = "online";
  $("#connection-label").textContent = "Provider configured";
}
updateOutputSummary();
