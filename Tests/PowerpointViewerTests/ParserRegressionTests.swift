import XCTest
import SwiftUI
@testable import PowerpointViewer

/// Regression tests for parser invariants.
///
/// Each test encodes a bug that shipped once. If one fails, do not "adjust the
/// test" — the renderer regressed the same way it did the first time.
final class ParserRegressionTests: XCTestCase {

    // MARK: - Helpers

    private func parse(_ url: URL) throws -> Presentation {
        let root = try Unzip.extract(url)
        defer { try? FileManager.default.removeItem(at: root) }
        return try PPTXParser(extractedRoot: root).parse()
    }

    private func fixtureURL() throws -> URL {
        guard let url = Bundle.module.url(forResource: "fixture", withExtension: "pptx",
                                          subdirectory: "Fixtures") else {
            throw XCTSkip("fixture.pptx missing — run Tools/make_fixture.py")
        }
        return url
    }

    /// Real-world decks live outside the repo (copyrighted content); their
    /// tests run when present and skip cleanly elsewhere.
    private func realDeck(_ name: String) throws -> URL {
        let url = FileManager.default.homeDirectoryForCurrentUser
            .appendingPathComponent("Downloads/\(name)")
        try XCTSkipUnless(FileManager.default.fileExists(atPath: url.path),
                          "\(name) not present in ~/Downloads")
        return url
    }

    private func textBoxes(in slide: Slide) -> [TextBox] {
        slide.elements.compactMap { if case .text(let t) = $0 { return t } else { return nil } }
    }

    private func tables(in slide: Slide) -> [TableElement] {
        slide.elements.compactMap { if case .table(let t) = $0 { return t } else { return nil } }
    }

    private func images(in slide: Slide) -> [PictureElement] {
        slide.elements.compactMap { if case .image(let p) = $0 { return p } else { return nil } }
    }

    // MARK: - Fixture deck (always runs)

    /// Bug: Foundation XPath escaped the context node, so a white background
    /// picked up the table's black text color and rendered slides black.
    func testBackgroundColorNotStolenFromTableText() throws {
        let pres = try parse(try fixtureURL())
        guard case .color(let bg)? = pres.slides[0].background else {
            return XCTFail("slide 1 should have a solid (white) background")
        }
        XCTAssertNotEqual("\(bg)", "\(Color.black)",
                          "XPath context-escape regression: bg stole table text color")
    }

    /// Bug: libxml2 drops whitespace-only <a:t> runs, gluing words together.
    func testWhitespaceOnlyRunsSurvive() throws {
        let pres = try parse(try fixtureURL())
        let text = textBoxes(in: pres.slides[1])
            .flatMap(\.paragraphs).flatMap(\.runs).map(\.text).joined()
        XCTAssertTrue(text.contains("kami'y nagpapasalamat"),
                      "space between runs lost — sentinel workaround regressed (got: \(text))")
    }

    /// Bug: a hardcoded .leading frame alignment overrode algn="ctr".
    func testCenteredParagraphStaysCentered() throws {
        let pres = try parse(try fixtureURL())
        let paras = textBoxes(in: pres.slides[1]).flatMap(\.paragraphs)
            .filter { !$0.runs.filter({ !$0.text.trimmingCharacters(in: .whitespaces).isEmpty }).isEmpty }
        XCTAssertTrue(paras.contains { $0.alignment == .center },
                      "centered paragraph parsed as non-centered")
    }

    func testFixtureTableStructure() throws {
        let pres = try parse(try fixtureURL())
        let table = try XCTUnwrap(tables(in: pres.slides[0]).first)
        XCTAssertEqual(table.columnWidths.count, 3)
        XCTAssertEqual(table.rows.count, 2)
        let header = table.rows[0].cells[0].paragraphs.flatMap(\.runs)
        XCTAssertTrue(header.contains { $0.bold && $0.underline }, "header run styling lost")
    }

    func testFixtureShapesAndRunStyling() throws {
        let pres = try parse(try fixtureURL())
        let slide2Runs = textBoxes(in: pres.slides[1]).flatMap(\.paragraphs).flatMap(\.runs)
        let styled = try XCTUnwrap(slide2Runs.first { $0.text.contains("kami") })
        XCTAssertEqual(styled.fontSize, 24)
        XCTAssertTrue(styled.bold)
        XCTAssertEqual(styled.fontName, "Arial")

        let shapes = pres.slides[2].elements.compactMap { el -> ShapeElement? in
            if case .shape(let s) = el { return s } else { return nil }
        }
        XCTAssertTrue(shapes.contains { $0.geometry == .roundedRectangle })
        XCTAssertTrue(shapes.contains { $0.geometry == .ellipse })
    }

    // MARK: - Real decks (skip when absent)

    /// The centered-lyrics regression, on the deck where you saw it.
    func testPanataAwitLyricsCentered() throws {
        let pres = try parse(try realDeck("HymnLyrics_PanataAwit_TAGALOG.pptx"))
        XCTAssertEqual(pres.slides.count, 127)
        let lyric = try XCTUnwrap(textBoxes(in: pres.slides[4]).first { $0.frame.width > 400 })
        XCTAssertTrue(lyric.paragraphs.allSatisfy { $0.alignment == .center },
                      "hymn lyrics must be centered")
        let run = try XCTUnwrap(lyric.paragraphs.first?.runs.first)
        XCTAssertTrue(run.bold)
        XCTAssertEqual(run.fontName, "Arial")
    }

    /// Slide 1 of the same deck: white bg + 3-column index table, all 13 rows.
    func testPanataAwitIndexTable() throws {
        let pres = try parse(try realDeck("HymnLyrics_PanataAwit_TAGALOG.pptx"))
        guard case .color? = pres.slides[0].background else {
            return XCTFail("index slide background should be a solid color (white)")
        }
        let table = try XCTUnwrap(tables(in: pres.slides[0]).first)
        XCTAssertEqual(table.columnWidths.count, 3)
        XCTAssertEqual(table.rows[0].cells[0].paragraphs.count, 13,
                       "index rows lost (whitespace or paragraph parsing regression)")
    }

    /// Master-inherited copyright overlay + transitions on the Anniversary deck.
    func testAnniversaryMasterOverlayAndTransitions() throws {
        let pres = try parse(try realDeck("HymnLyrics_AnniversaryThanksgiving2026_TAGALOG.pptx"))
        XCTAssertEqual(pres.slides.count, 77)
        let slide4 = pres.slides[3]
        XCTAssertTrue(slide4.hasTransition)
        guard case .image? = slide4.background else {
            return XCTFail("background image from master lost")
        }
        XCTAssertFalse(images(in: slide4).isEmpty, "master copyright overlay (image2.png) lost")
        let title = try XCTUnwrap(textBoxes(in: slide4).first { $0.frame.width < 400 })
        let titleRun = try XCTUnwrap(title.paragraphs.first?.runs.first)
        XCTAssertEqual(titleRun.fontSize, 15, "lstStyle default size lost")
        XCTAssertTrue(titleRun.shadow, "outer shadow effect (defRPr effectLst) lost")
    }

    /// Builds, paragraph-level reveals, picture crops, and ❖ bullets.
    func testPangkalahatangAnimationsCropsBullets() throws {
        let pres = try parse(try realDeck("PANGKALAHATANG PULONG_011226.pptx"))
        XCTAssertEqual(pres.slides.count, 36, "permissions normalization regressed (0 slides)")

        // Slide 8: three paragraph-level build steps on one shape.
        let steps = pres.slides[7].buildSteps
        XCTAssertEqual(steps.count, 3)
        XCTAssertTrue(steps.allSatisfy { !$0.paragraphReveals.isEmpty },
                      "paragraph-level (pRg) builds lost")

        // Slide 13: the two posters are cropped screenshots.
        let pics = images(in: pres.slides[12]).filter { !$0.crop.isZero }
        XCTAssertEqual(pics.count, 2, "srcRect crops lost")

        // Slide 12: Wingdings 'v' must map to ❖.
        let bullets = textBoxes(in: pres.slides[11]).flatMap(\.paragraphs).compactMap(\.bullet)
        XCTAssertTrue(bullets.contains { $0.glyph == "\u{2756}" },
                      "Wingdings bullet mapping lost")
    }
}
