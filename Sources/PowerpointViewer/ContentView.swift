import SwiftUI
import UniformTypeIdentifiers
import AppKit

struct ContentView: View {
    @ObservedObject var store: PresentationStore
    @State private var isDropTargeted = false

    var body: some View {
        Group {
            if store.presentation != nil {
                loadedView
            } else {
                EmptyStateView(store: store, isDropTargeted: $isDropTargeted)
            }
        }
        .frame(minWidth: 720, minHeight: 480)
        .onDrop(of: [.fileURL], isTargeted: $isDropTargeted) { providers in
            handleDrop(providers)
        }
        .alert("Could not open presentation",
               isPresented: Binding(get: { store.errorMessage != nil },
                                    set: { if !$0 { store.errorMessage = nil } })) {
            Button("OK", role: .cancel) { store.errorMessage = nil }
        } message: {
            Text(store.errorMessage ?? "")
        }
    }

    // MARK: - Loaded layout

    private var loadedView: some View {
        NavigationSplitView {
            ThumbnailSidebar(store: store)
                .navigationSplitViewColumnWidth(min: 160, ideal: 200, max: 260)
        } detail: {
            if store.isPresenting {
                PresenterConsoleView(store: store)
            } else {
                VStack(spacing: 0) {
                    if let slide = store.currentSlide, let pres = store.presentation {
                        SlideView(slide: slide, slideSize: pres.size)
                            .padding(24)
                            .frame(maxWidth: .infinity, maxHeight: .infinity)
                            .background(Color(nsColor: .windowBackgroundColor))
                    }
                    Divider()
                    SlideControls(store: store)
                }
            }
        }
        .navigationTitle(store.fileName ?? "PowerPoint Viewer")
        .toolbar {
            ToolbarItem(placement: .primaryAction) {
                Button {
                    openPanel()
                } label: {
                    Label("Open", systemImage: "folder")
                }
            }
            ToolbarItem(placement: .primaryAction) {
                Button {
                    if store.isPresenting { store.endPresentation() }
                    else { store.startPresentation() }
                } label: {
                    Label(store.isPresenting ? "End" : "Present",
                          systemImage: store.isPresenting ? "stop.fill" : "play.fill")
                }
            }
        }
    }

    // MARK: - Actions

    private func openPanel() {
        let panel = NSOpenPanel()
        panel.allowsMultipleSelection = false
        panel.canChooseDirectories = false
        if let pptx = UTType(filenameExtension: "pptx") {
            panel.allowedContentTypes = [pptx]
        }
        if panel.runModal() == .OK, let url = panel.url {
            store.open(url: url)
        }
    }

    private func handleDrop(_ providers: [NSItemProvider]) -> Bool {
        guard let provider = providers.first else { return false }
        _ = provider.loadObject(ofClass: URL.self) { url, _ in
            guard let url, url.pathExtension.lowercased() == "pptx" else { return }
            DispatchQueue.main.async { store.open(url: url) }
        }
        return true
    }
}

// MARK: - Empty state

struct EmptyStateView: View {
    @ObservedObject var store: PresentationStore
    @Binding var isDropTargeted: Bool

    var body: some View {
        VStack(spacing: 20) {
            Image(systemName: "rectangle.on.rectangle.angled")
                .font(.system(size: 64))
                .foregroundStyle(.secondary)
            Text("PowerPoint Viewer")
                .font(.largeTitle.weight(.semibold))
            Text("Drag a .pptx file here, or open one to begin.")
                .foregroundStyle(.secondary)
            if store.isLoading {
                ProgressView().padding(.top, 4)
            } else {
                Button("Open Presentation…") { openPanel() }
                    .controlSize(.large)
                    .buttonStyle(.borderedProminent)
            }
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
        .background(Color(nsColor: .windowBackgroundColor))
        .overlay {
            if isDropTargeted {
                RoundedRectangle(cornerRadius: 12)
                    .strokeBorder(Color.accentColor, style: StrokeStyle(lineWidth: 3, dash: [10]))
                    .padding(20)
            }
        }
    }

    private func openPanel() {
        let panel = NSOpenPanel()
        panel.allowsMultipleSelection = false
        panel.canChooseDirectories = false
        if let pptx = UTType(filenameExtension: "pptx") {
            panel.allowedContentTypes = [pptx]
        }
        if panel.runModal() == .OK, let url = panel.url {
            store.open(url: url)
        }
    }
}

// MARK: - Thumbnails

struct ThumbnailSidebar: View {
    @ObservedObject var store: PresentationStore

    var body: some View {
        ScrollViewReader { proxy in
            List(selection: Binding(
                get: { store.currentIndex },
                set: { if let v = $0 { store.go(to: v) } })
            ) {
                if let pres = store.presentation {
                    ForEach(Array(pres.slides.enumerated()), id: \.offset) { index, slide in
                        HStack(spacing: 8) {
                            Text("\(index + 1)")
                                .font(.caption.monospacedDigit())
                                .foregroundStyle(.secondary)
                                .frame(width: 22, alignment: .trailing)
                            SlideView(slide: slide, slideSize: pres.size)
                                .frame(width: 150, height: 150 * pres.size.height / pres.size.width)
                                .background(Color.white)
                                .clipShape(RoundedRectangle(cornerRadius: 4))
                                .overlay(
                                    RoundedRectangle(cornerRadius: 4)
                                        .strokeBorder(index == store.currentIndex ? Color.accentColor : Color.gray.opacity(0.3),
                                                      lineWidth: index == store.currentIndex ? 2 : 1)
                                )
                        }
                        .padding(.vertical, 2)
                        .tag(index)
                        .id(index)
                    }
                }
            }
            .onChange(of: store.currentIndex) { _, newValue in
                withAnimation { proxy.scrollTo(newValue, anchor: .center) }
            }
        }
    }
}

// MARK: - Bottom controls

struct SlideControls: View {
    @ObservedObject var store: PresentationStore

    var body: some View {
        HStack {
            Button {
                store.goPrevious()
            } label: {
                Image(systemName: "chevron.left")
            }
            .disabled(store.currentIndex <= 0)

            Text("Slide \(store.currentIndex + 1) of \(store.slideCount)")
                .font(.callout.monospacedDigit())
                .frame(minWidth: 140)

            Button {
                store.goNext()
            } label: {
                Image(systemName: "chevron.right")
            }
            .disabled(store.currentIndex >= store.slideCount - 1)

            Spacer()

            Button {
                store.startPresentation()
            } label: {
                Label("Present", systemImage: "play.fill")
            }
        }
        .padding(.horizontal, 16)
        .padding(.vertical, 8)
        .background(.bar)
    }
}

// MARK: - Presenter console

/// Shown in the main window while presenting: the slide the audience currently
/// sees, a preview of the next slide, and transport controls.
struct PresenterConsoleView: View {
    @ObservedObject var store: PresentationStore
    @StateObject private var camera = CameraManager()

    var body: some View {
        VStack(spacing: 0) {
            HSplitView {
                // Current slide (what the audience sees now).
                VStack(alignment: .leading, spacing: 8) {
                    HStack {
                        Label("Now Presenting — Slide \(store.currentIndex + 1) of \(store.slideCount)",
                              systemImage: "dot.radiowaves.left.and.right")
                            .font(.headline)
                            .foregroundStyle(.red)
                        Spacer()
                        if let start = store.presentationStartDate {
                            Label {
                                Text(start, style: .timer)
                                    .font(.headline.monospacedDigit())
                            } icon: {
                                Image(systemName: "stopwatch")
                            }
                            .foregroundStyle(.secondary)
                            .help("Elapsed presentation time")
                        }
                    }
                    if let slide = store.currentSlide, let pres = store.presentation {
                        SlideView(slide: slide, slideSize: pres.size, showShadow: false)
                            .background(Color.black)
                            .clipShape(RoundedRectangle(cornerRadius: 6))
                            .overlay(RoundedRectangle(cornerRadius: 6)
                                .strokeBorder(Color.red.opacity(0.7), lineWidth: 2))
                    }
                    Spacer(minLength: 0)
                }
                .padding(16)
                .frame(minWidth: 340, maxWidth: .infinity, maxHeight: .infinity, alignment: .top)

                // Right column: next slide over camera, each pane resizable.
                VSplitView {
                    nextSlidePane
                        .padding(12)
                        .frame(minHeight: 150, maxHeight: .infinity)
                    cameraPane
                        .padding(12)
                        .frame(minHeight: 110, maxHeight: .infinity)
                }
                .frame(minWidth: 240, idealWidth: 330, maxWidth: 640)
            }
            .frame(maxWidth: .infinity, maxHeight: .infinity)

            Divider()

            // Transport controls.
            HStack(spacing: 12) {
                Button {
                    store.goPrevious()
                } label: {
                    Image(systemName: "chevron.left")
                }
                .disabled(store.currentIndex <= 0)

                Text("Slide \(store.currentIndex + 1) of \(store.slideCount)")
                    .font(.callout.monospacedDigit())
                    .frame(minWidth: 140)

                Button {
                    store.goNext()
                } label: {
                    Image(systemName: "chevron.right")
                }
                .disabled(store.currentIndex >= store.slideCount - 1)

                Text("← → to navigate · esc to end")
                    .font(.caption)
                    .foregroundStyle(.tertiary)
                    .padding(.leading, 8)

                Spacer()

                Button(role: .destructive) {
                    store.endPresentation()
                } label: {
                    Label("End Presentation", systemImage: "stop.fill")
                }
            }
            .padding(.horizontal, 16)
            .padding(.vertical, 10)
            .background(.bar)
        }
        .background(Color(nsColor: .underPageBackgroundColor))
        .onDisappear { camera.stop() }
    }

    // MARK: Next slide pane

    private var nextSlidePane: some View {
        VStack(alignment: .leading, spacing: 8) {
            Label(store.nextSlide != nil ? "Next — Slide \(store.currentIndex + 2)" : "Next",
                  systemImage: "forward.end")
                .font(.headline)
                .foregroundStyle(.secondary)
            if let next = store.nextSlide, let pres = store.presentation {
                SlideView(slide: next, slideSize: pres.size, showShadow: false)
                    .background(Color.black)
                    .clipShape(RoundedRectangle(cornerRadius: 6))
                    .overlay(RoundedRectangle(cornerRadius: 6)
                        .strokeBorder(Color.gray.opacity(0.4), lineWidth: 1))
            } else {
                RoundedRectangle(cornerRadius: 6)
                    .fill(Color.black.opacity(0.85))
                    .overlay(
                        Text("End of presentation")
                            .font(.callout)
                            .foregroundStyle(.secondary)
                    )
            }
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .top)
    }

    // MARK: Camera pane

    private var cameraPane: some View {
        VStack(alignment: .leading, spacing: 8) {
            HStack {
                Label("Camera", systemImage: "video")
                    .font(.headline)
                    .foregroundStyle(.secondary)
                Spacer()
                Toggle("", isOn: $camera.isEnabled)
                    .toggleStyle(.switch)
                    .controlSize(.small)
                    .labelsHidden()
            }

            if camera.isEnabled {
                if camera.devices.count > 1 {
                    Picker("", selection: $camera.selectedID) {
                        ForEach(camera.devices, id: \.uniqueID) { device in
                            Text(device.localizedName).tag(device.uniqueID)
                        }
                    }
                    .labelsHidden()
                    .controlSize(.small)
                }

                if camera.authorizationDenied {
                    RoundedRectangle(cornerRadius: 6)
                        .fill(Color.black.opacity(0.85))
                        .overlay(
                            Text("Camera access denied.\nEnable it in System Settings → Privacy & Security → Camera.")
                                .font(.caption)
                                .multilineTextAlignment(.center)
                                .foregroundStyle(.secondary)
                                .padding(8)
                        )
                } else if camera.devices.isEmpty {
                    RoundedRectangle(cornerRadius: 6)
                        .fill(Color.black.opacity(0.85))
                        .overlay(
                            Text("No camera detected")
                                .font(.caption)
                                .foregroundStyle(.secondary)
                        )
                } else {
                    CameraPreviewView(session: camera.session)
                        .clipShape(RoundedRectangle(cornerRadius: 6))
                        .overlay(RoundedRectangle(cornerRadius: 6)
                            .strokeBorder(Color.gray.opacity(0.4), lineWidth: 1))
                }
            } else {
                RoundedRectangle(cornerRadius: 6)
                    .fill(Color.black.opacity(0.5))
                    .overlay(
                        Label("Camera off", systemImage: "video.slash")
                            .font(.caption)
                            .foregroundStyle(.secondary)
                    )
            }
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .top)
        .onChange(of: camera.isEnabled) { _, on in
            if on {
                camera.refreshDevices()
                camera.start()
            } else {
                camera.stop()
            }
        }
        .onChange(of: camera.selectedID) { _, _ in
            if camera.isEnabled { camera.applySelectionAndRun() }
        }
    }
}
