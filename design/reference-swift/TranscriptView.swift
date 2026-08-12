import SwiftUI

/// One speaker's consecutive utterances. Build these by collapsing runs of
/// same-speaker segments — the design shows "them" saying two things under a
/// single THEM header, not two separate turns.
struct Turn: Identifiable {
    let id = UUID()
    let speaker: String     // "me" | "them"
    let time: String        // "0:00"
    let lines: [String]
    var isMe: Bool { speaker == "me" }
}

/// Groups consecutive same-speaker segments into turns.
func groupedTurns(_ segments: [SessionStore.Segment]) -> [Turn] {
    var out: [Turn] = []
    for seg in segments {
        if let last = out.last, last.speaker == seg.speaker {
            out[out.count - 1] = Turn(speaker: last.speaker,
                                      time: last.time,
                                      lines: last.lines + [seg.text])
        } else {
            out.append(Turn(speaker: seg.speaker, time: seg.time, lines: [seg.text]))
        }
    }
    return out
}

struct TranscriptView: View {
    let turns: [Turn]
    /// Search term for highlighting, or nil.
    var highlight: String? = nil

    var body: some View {
        GeometryReader { geo in
            let cap = (geo.size.width - PT.M.transcriptPad * 2) * PT.M.turnMaxWidthFraction
            ScrollView {
                VStack(spacing: PT.M.turnGap) {
                    ForEach(turns) { turn in
                        TurnView(turn: turn, maxWidth: cap, highlight: highlight)
                    }
                }
                .padding(PT.M.transcriptPad)
                .frame(maxWidth: .infinity)
            }
        }
        .background(PT.C.window)
    }
}

private struct TurnView: View {
    let turn: Turn
    let maxWidth: CGFloat
    let highlight: String?

    var body: some View {
        VStack(alignment: turn.isMe ? .trailing : .leading,
               spacing: turn.isMe ? 7 : 8) {

            // Header: me reads "0:00 ME" (time first, toward the edge);
            // them reads "THEM 0:00". The mirror is intentional.
            HStack(spacing: 8) {
                if turn.isMe { time }
                Text(turn.speaker.uppercased())
                    .font(PT.F.speaker)
                    .tracking(PT.F.labelTracking)
                    .foregroundStyle(turn.isMe ? PT.C.signalLit : PT.C.speakerThem)
                if !turn.isMe { time }
            }

            ForEach(Array(turn.lines.enumerated()), id: \.offset) { _, line in
                Text(line)
                    .font(PT.F.transcript)
                    .lineSpacing(PT.F.transcriptLineSpacing)
                    .foregroundStyle(turn.isMe ? PT.C.text : PT.C.text2)
                    // Body copy stays LEFT-aligned for both speakers. Only the
                    // block is right-aligned; right-aligned prose reads slower.
                    .multilineTextAlignment(.leading)
                    .textSelection(.enabled)
                    .fixedSize(horizontal: false, vertical: true)
                    .padding(turn.isMe
                             ? EdgeInsets(top: PT.M.bubblePadV, leading: PT.M.bubblePadH,
                                          bottom: PT.M.bubblePadV, trailing: PT.M.bubblePadH)
                             : EdgeInsets())
                    .background(turn.isMe ? PT.C.surface : .clear,
                                in: RoundedRectangle(cornerRadius: PT.M.bubbleRadius))
            }
        }
        .frame(maxWidth: maxWidth, alignment: turn.isMe ? .trailing : .leading)
        .frame(maxWidth: .infinity, alignment: turn.isMe ? .trailing : .leading)
    }

    private var time: some View {
        Text(turn.time)
            .font(PT.F.monoTiny)
            .foregroundStyle(PT.C.text5)
    }
}
