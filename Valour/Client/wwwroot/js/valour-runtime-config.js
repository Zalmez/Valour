// Runtime-deploy config. Leave blank for same-origin API.
window.valourRuntimeConfig = window.valourRuntimeConfig || {};
if (typeof window.valourRuntimeConfig.apiOrigin !== "string")
    window.valourRuntimeConfig.apiOrigin = "";
