window.copyToClipboard = function (text) {
    return navigator.clipboard && navigator.clipboard.writeText(text);
};

// Loest im Browser einen Datei-Download aus, ohne Server-Roundtrip.
// content: string, fileName: string, mimeType: string
window.downloadFile = function (fileName, mimeType, content) {
    const blob = new Blob([content], { type: mimeType || "text/plain" });
    const url = URL.createObjectURL(blob);
    const a = document.createElement("a");
    a.href = url;
    a.download = fileName || "download.txt";
    document.body.appendChild(a);
    a.click();
    document.body.removeChild(a);
    setTimeout(() => URL.revokeObjectURL(url), 1000);
};
