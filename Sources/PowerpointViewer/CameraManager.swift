import AVFoundation
import SwiftUI

/// Discovers attached cameras and runs a capture session for the presenter
/// console's camera preview.
///
/// All session mutation happens on a private serial queue (Apple's recommended
/// pattern — `startRunning()` blocks), while published state updates on main.
final class CameraManager: NSObject, ObservableObject {
    @Published var devices: [AVCaptureDevice] = []
    @Published var selectedID: String = ""
    @Published var isEnabled: Bool = false
    @Published var authorizationDenied: Bool = false

    let session = AVCaptureSession()
    private let sessionQueue = DispatchQueue(label: "PowerpointViewer.camera.session")

    /// Enumerates built-in, external (USB), and Continuity cameras.
    func refreshDevices() {
        let types: [AVCaptureDevice.DeviceType] = [
            .builtInWideAngleCamera, .external, .continuityCamera
        ]
        let discovery = AVCaptureDevice.DiscoverySession(
            deviceTypes: types, mediaType: .video, position: .unspecified)
        devices = discovery.devices
        if selectedID.isEmpty || !devices.contains(where: { $0.uniqueID == selectedID }) {
            selectedID = devices.first?.uniqueID ?? ""
        }
    }

    func start() {
        switch AVCaptureDevice.authorizationStatus(for: .video) {
        case .authorized:
            applySelectionAndRun()
        case .notDetermined:
            AVCaptureDevice.requestAccess(for: .video) { [weak self] granted in
                DispatchQueue.main.async {
                    guard let self else { return }
                    if granted {
                        self.refreshDevices()
                        self.applySelectionAndRun()
                    } else {
                        self.authorizationDenied = true
                        self.isEnabled = false
                    }
                }
            }
        default:
            authorizationDenied = true
            isEnabled = false
        }
    }

    /// Rebuilds the session input for the currently selected device and runs.
    func applySelectionAndRun() {
        let targetID = selectedID
        let device = devices.first(where: { $0.uniqueID == targetID })
            ?? AVCaptureDevice.default(for: .video)
        sessionQueue.async { [session] in
            session.beginConfiguration()
            session.inputs.forEach { session.removeInput($0) }
            if let device, let input = try? AVCaptureDeviceInput(device: device),
               session.canAddInput(input) {
                session.addInput(input)
            }
            session.commitConfiguration()
            if !session.isRunning { session.startRunning() }
        }
    }

    func stop() {
        sessionQueue.async { [session] in
            if session.isRunning { session.stopRunning() }
        }
    }
}

/// Live camera preview backed by `AVCaptureVideoPreviewLayer`.
struct CameraPreviewView: NSViewRepresentable {
    let session: AVCaptureSession

    func makeNSView(context: Context) -> NSView {
        let view = NSView()
        view.wantsLayer = true
        let preview = AVCaptureVideoPreviewLayer(session: session)
        preview.videoGravity = .resizeAspect   // letterbox: show full frame, no crop
        preview.frame = view.bounds
        preview.autoresizingMask = [.layerWidthSizable, .layerHeightSizable]
        view.layer = CALayer()
        view.layer?.addSublayer(preview)
        view.layer?.backgroundColor = NSColor.black.cgColor
        return view
    }

    func updateNSView(_ nsView: NSView, context: Context) {
        nsView.layer?.sublayers?.first?.frame = nsView.bounds
    }
}
