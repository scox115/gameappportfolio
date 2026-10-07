// Saves bytes the game downloaded as a file on the player's computer (used by "Download my data").
window.gameFiles = {
    save(fileName, contentType, bytes) {
        const url = URL.createObjectURL(new Blob([bytes], { type: contentType }));
        const link = document.createElement("a");
        link.href = url;
        link.download = fileName;
        document.body.appendChild(link);
        link.click();
        link.remove();
        setTimeout(() => URL.revokeObjectURL(url), 1000);
    }
};
