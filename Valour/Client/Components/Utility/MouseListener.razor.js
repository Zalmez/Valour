export const init = (dotnet) => {
    const service = {
        lastX: null,
        lastY: null,
        // Drag mode state
        dragElement: null,
        dragScannerRef: null,
        moveListener: async (e) => {
            document.body.classList.add('no-select');
            if (e.clientX === service.lastX && e.clientY === service.lastY) {
                return;
            }
            const deltaX = service.lastX ? e.clientX - service.lastX : 0;
            const deltaY = service.lastY ? e.clientY - service.lastY : 0;
            service.lastX = e.clientX;
            service.lastY = e.clientY;
            // JS-side drag mode: move element directly, no .NET interop
            if (service.dragElement) {
                let newX = parseFloat(service.dragElement.style.left) + deltaX;
                let newY = parseFloat(service.dragElement.style.top) + deltaY;
                // Bounds checking (equivalent to EnsureOnScreen)
                const width = service.dragElement.offsetWidth;
                const height = service.dragElement.offsetHeight;
                if (newX < 0)
                    newX = 0;
                if (newY < 0)
                    newY = 0;
                if (newX + width > window.innerWidth)
                    newX = window.innerWidth - width;
                if (newY + height > window.innerHeight)
                    newY = window.innerHeight - height;
                service.dragElement.style.left = newX + 'px';
                service.dragElement.style.top = newY + 'px';
                // Run target scanner (it has its own internal throttle)
                if (service.dragScannerRef) {
                    service.dragScannerRef.scan(e.clientX, e.clientY);
                }
                return; // Skip .NET interop
            }
            await dotnet.invokeMethodAsync('NotifyMouseMove', e.clientX, e.clientY, e.pageX, e.pageY, deltaX, deltaY);
        },
        startMoveListener: () => {
            document.addEventListener('mousemove', service.moveListener);
        },
        stopMoveListener: () => {
            document.removeEventListener('mousemove', service.moveListener);
            service.lastX = null;
            service.lastY = null;
        },
        upListener: async (e) => {
            document.body.classList.remove('no-select');
            await dotnet.invokeMethodAsync('NotifyMouseUp', e.clientX, e.clientY);
        },
        startUpListener: () => {
            document.addEventListener('mouseup', service.upListener);
        },
        stopUpListener: () => {
            document.removeEventListener('mouseup', service.upListener);
        },
        startDrag: (elementId, scannerRef) => {
            const el = document.getElementById(elementId);
            if (el) {
                service.dragElement = el;
                service.dragScannerRef = scannerRef;
            }
        },
        stopDrag: () => {
            if (service.dragElement) {
                const x = parseFloat(service.dragElement.style.left) || 0;
                const y = parseFloat(service.dragElement.style.top) || 0;
                service.dragElement = null;
                service.dragScannerRef = null;
                return [x, y];
            }
            return null;
        }
    };
    return service;
};
//# sourceMappingURL=MouseListener.razor.js.map