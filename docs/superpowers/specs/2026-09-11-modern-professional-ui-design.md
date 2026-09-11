# MT5 Manager Modern Professional UI Design

## Goal

Replace the generic utility appearance with a modern, professional light dashboard while preserving every existing operation, binding, safety gate, and automation name.

## Visual language

- Windows-native Segoe UI typography with a 4/8 px spacing rhythm.
- Slate-tinted application background, white elevated surfaces, blue primary accent.
- Semantic success, warning, and danger colors; state always remains readable as text rather than color alone.
- Rounded 8–12 px surfaces, thin cool-gray borders, and restrained shadows.
- Minimum 44 px primary interactive targets, visible keyboard focus, clear disabled states.
- No external UI framework, custom font, decorative animation, emoji icon, or behavioral change.

## Main window

Use a two-part header: product identity and explanatory copy above a toolbar containing search, scan, and manual registration. Render terminals as spacious cards. Each card exposes terminal name and textual state first, executable/data paths second, runtime/storage metadata third, then operation feedback and actions. Keep Start, Stop, and Restart together; visually separate Cleanup as the destructive data action. Global errors use a bordered notification surface. The list remains virtualized and resizes with the window.

## Cleanup dialog

Present the existing workflow as four visually ordered stages: choose categories, acknowledge permanent deletion, inspect preview, then prepare/continue. Categories use large check rows. Preview, error, and result each have dedicated surfaces. Force continuation uses danger styling and only appears under the existing view-model condition. Closing remains immediately discoverable and all existing cancellation semantics remain unchanged.

## Manual registration dialog

Use vertical fields with persistent labels and helper text. The validation message receives its own neutral status surface. Cancel is secondary and Register is the sole primary CTA. Existing validation and registration behavior remain unchanged.

## Accessibility and compatibility

Preserve accessible names, label targets, tab order, button captions, command bindings, click handlers, default/cancel behavior, and minimum window dimensions. Long paths truncate only where the full value remains available as a tooltip. Text wrapping prevents clipping under Windows text scaling. Styling is implemented entirely in `App.xaml` and the three existing window XAML files.

## Verification

Run all existing tests and a Release build. Publish with the win-x64 profile. Launch the published executable and inspect the actual WPF surfaces for clipping, overflow, enabled states, focus order, modal behavior, and clean shutdown. Use UI Automation geometry when image interpretation is unavailable.
