window.formatLocalTimestamp = (utcValue) => {
    const formatter = new Intl.DateTimeFormat(undefined, { dateStyle: "medium", timeStyle: "long" });
    return `${formatter.format(new Date(utcValue))} (${formatter.resolvedOptions().timeZone})`;
};

window.scrollLogsToBottom = (container) => {
    container.scrollTop = container.scrollHeight;
};

window.downloadFile = (filename, mimeType, base64Content) => {
    const link = document.createElement('a');
    link.href = `data:${mimeType};base64,${base64Content}`;
    link.download = filename;
    link.click();
};
