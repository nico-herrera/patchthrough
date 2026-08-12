import SwiftUI

/// Sidebar session row. Two lines: time + duration, then the first thing said.
/// "9:45 PM" alone does not identify a meeting — the first line does.
struct SessionRow: View {
    let item: SessionStore.Item
    let isSelected: Bool

    var body: some View {
        VStack(alignment: .leading, spacing: 3) {
            HStack(alignment: .firstTextBaseline, spacing: 7) {
                Text(item.timeLabel)
                    .font(isSelected ? PT.F.sessionTime : PT.F.sessionTime2)
                    .foregroundStyle(isSelected ? PT.C.text : PT.C.text2)
                Text(item.duration)
                    .font(PT.F.monoSmall)
                    .foregroundStyle(isSelected ? PT.C.signalInk : PT.C.text4)
            }
            Text(item.firstLine)
                .font(PT.F.sessionLine)
                .foregroundStyle(isSelected ? PT.C.textSel : PT.C.text4)
                .lineLimit(1)
                .truncationMode(.tail)
        }
        .padding(9)
        .frame(maxWidth: .infinity, alignment: .leading)
        .background(
            RoundedRectangle(cornerRadius: PT.M.rowRadius)
                .fill(isSelected ? PT.C.selectFill : .clear)
                .strokeBorder(isSelected ? PT.C.selectStroke : .clear, lineWidth: 1)
        )
    }
}

// If you use native List selection instead, set .tint(PT.C.signal) on the
// List and skip the .background above — but do NOT draw a leading edge bar.
// Every left-border highlight was removed from this design on purpose.
