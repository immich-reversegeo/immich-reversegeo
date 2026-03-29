window.downloadFile = (filename, mimeType, base64Content) => {
    const link = document.createElement('a');
    link.href = `data:${mimeType};base64,${base64Content}`;
    link.download = filename;
    link.click();
};
