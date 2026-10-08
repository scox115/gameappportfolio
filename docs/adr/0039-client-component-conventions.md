# 0039. Keep styles, markup and logic apart in the Blazor client

- **Status:** Accepted
- **Date:** 2026-10-08

## Context

The client grew screen by screen. By the time it had a shop, PvP, friends, spectating and replays, two pages held most of it: `Index.razor` (sign-in, town, leaderboards and the boss battle) and `PvpArena.razor` (the lobby, the duel and its result), about 1,100 and 600 lines, each mixing markup, inline `style="..."` attributes and C# in one file. Colours were repeated as hex values across dozens of inline styles, and `app.css` had grown into a mix of base styles and rules for single components, with `!important` overrides to win against the inline ones on phones. A change to one screen meant scrolling past four others, and nothing told a reader where a new rule or method should go.

## Decision

- **Styles live in CSS isolation files.** Each component's styles go in its own `X.razor.css`, which Blazor scopes to that component at build time, so a rule can't leak into another screen. A parent styles markup inside a child component only through `::deep`, from an element the parent owns.
- **`app.css` holds only what is shared:** the colour and font tokens on `:root` (`--color-page`, `--color-accent`, `--color-gold` and the rest), base element styles, and the few rules more than one component uses (the password field, dialog inputs, the watch and replay panels). Components use the tokens, never raw hex values.
- **Inline styles only for values known at run time,** and then only as CSS custom properties the component's stylesheet reads: a health bar's width (`--hp`), a hero's colour, a cosmetic frame. A value with two or three states is a modifier class (`is-waiting`, `is-won`) instead.
- **Pages are thin.** A page decides which screen to show and owns the state shared between them; each screen is its own component under `Shared/`, taking `[Parameter]`s and raising `EventCallback`s rather than reaching into its parent. `Index.razor` is now a 35-line switch over `SignInPanel`, `TownDashboard`, `LeaderboardView` and `BossBattle`; `PvpArena.razor` hands the lobby, each player's panel and the result to `DuelLobby`, `DuelPlayerPanel` and `DuelResult`, and keeps the SignalR connection.
- **C# goes in a code-behind file** for pages and for any component with more than a few lines of logic: `X.razor.cs` declares `public partial class X`, with services as `[Inject]` properties. The `.razor` file is markup only.
- **Data shapes live in `Models/`,** one file per area (`AccountModels`, `BattleModels`, `DuelModels`), instead of as nested records at the bottom of a page.

## Alternatives considered

- **Leave it as it was.** It worked and the tests passed, but every new feature made the two big pages harder to read and review, and the inline colours made a palette change a search-and-replace across the client.
- **A CSS framework or utility classes (Tailwind).** Would replace the inline styles with class lists, but adds a build step and a second styling vocabulary next to Bootstrap, for a client this size.
- **Code-behind for every component, however small.** A ten-line `@code` block next to its markup is easier to read than two files. Small components keep theirs, and move to code-behind when they grow.

## Consequences

- Each screen can be read, changed and reviewed on its own, and a style rule's reach is the component it sits beside.
- A palette change is an edit to the tokens in `app.css`.
- More files: most screens are now three (`.razor`, `.razor.cs`, `.razor.css`).
- Scoped CSS doesn't reach into child components, so a parent that needs to restyle one has to use `::deep` deliberately.
- The move was checked by capturing the computed style of about 2,800 elements across 33 screens before and after, and by the Playwright browser tests. The only intended change in behaviour: a leaderboard that fails to load now says so on its own screen.
