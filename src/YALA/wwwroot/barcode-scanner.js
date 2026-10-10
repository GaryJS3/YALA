let stream, timer, generation = 0;
export function isSupported() { return Boolean(navigator.mediaDevices?.getUserMedia); }
export async function start(video, dotNet) {
    stop(video);
    const run = generation;
    if (!window.isSecureContext) throw new Error('Camera access requires HTTPS. Open YALA using HTTPS or enter the barcode manually.');
    if (!isSupported()) throw new Error('Camera access is unavailable in this browser. Enter the barcode manually.');
    try {
        await import('./lib/zxing/index.min.js');
        const hints = new Map([[window.ZXing.DecodeHintType.POSSIBLE_FORMATS, ['EAN_13', 'EAN_8', 'UPC_A', 'UPC_E', 'ITF', 'CODE_128'].map(name => window.ZXing.BarcodeFormat[name])]]);
        const reader = new window.ZXing.BrowserMultiFormatReader(hints);
        if (run !== generation) return;
        const acquired = await navigator.mediaDevices.getUserMedia({ video: { facingMode: { ideal: 'environment' }, width: { ideal: 1280 }, height: { ideal: 720 } }, audio: false });
        if (run !== generation) { acquired.getTracks().forEach(track => track.stop()); return; }
        stream = acquired;
        video.srcObject = stream;
        await video.play();
        const scan = async () => {
            if (run !== generation) return;
            let value;
            if (video.readyState >= 2) {
                try { value = reader.decode(video).getText(); }
                catch (error) {
                    if (!(error instanceof window.ZXing.NotFoundException || error instanceof window.ZXing.ChecksumException || error instanceof window.ZXing.FormatException)) {
                        stop(video);
                        await dotNet.invokeMethodAsync('ScannerFailed', 'The camera could not read barcodes. Restart scanning or enter the barcode manually.');
                        return;
                    }
                }
            }
            if (value) { stop(video); await dotNet.invokeMethodAsync('BarcodeDetected', value); return; }
            timer = window.setTimeout(scan, 300);
        };
        if (run === generation) timer = window.setTimeout(scan, 0);
    } catch (error) {
        if (run !== generation) return;
        stop(video);
        if (error.name === 'NotAllowedError') throw new Error('Camera permission was denied. Allow camera access for YALA in your browser settings, then try again.');
        if (error.name === 'NotFoundError') throw new Error('No camera was found. Enter the barcode manually.');
        if (error.name === 'NotReadableError') throw new Error('The camera is busy or unavailable. Close other apps using it, then try again.');
        throw error;
    }
}
export function stop(video) {
    generation++;
    window.clearTimeout(timer);
    stream?.getTracks().forEach(track => track.stop());
    stream = undefined;
    if (video) video.srcObject = null;
}
