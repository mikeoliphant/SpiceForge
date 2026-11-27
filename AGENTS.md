# Notes for agents

Guidance for AI coding agents (and anyone else) working on SpiceForge.

## Keep the lessons learned up to date

`DesignDocs/LessonsLearned.md` is the project's memory: what was tried, what was measured, what worked and what didn't,
and why. Add to it as progress is made, not only at the end:

- **Record findings in the section they belong to**, or a new section if they start a new topic. Lead each point with a
  bold sentence that states the lesson, then the evidence.
- **Include the numbers.** Say what was measured, on which circuits and settings, and against what reference: error in
  dB, µs per sample, capped Newton steps, live reads. A lesson without its evidence can't be checked later.
- **Record what didn't work too**, and why. Reverted experiments save the next person from repeating them.
- **Keep "Open issues" current.** Add new ones, and reword or remove ones that progress has changed. Say what has
  been tried and what the next step would be.
- **Correct what turns out to be wrong.** If a later measurement contradicts an earlier lesson, fix the earlier one
  rather than leaving both.

## Keep the UI spec up to date

`DesignDocs/UISpec.md` describes the editor's UI as it is now. A change that alters what the UI does (a gesture, a
shortcut, a panel, a dialog, what's remembered) updates the spec in the same change, in the section it belongs to:

- **The spec says what the UI does; the lessons say why.** Behaviour goes in the spec. What was tried, what was
  measured and what didn't work stay in `LessonsLearned.md`, which can point to the spec rather than repeat it.
- **Describe our UI, not LiveSPICE's.** Where it departs from LiveSPICE, say so in the spec's last section.
- **Keep the Help dialog in step.** Its shortcut lists in `SpiceForgeWebUI/src/help.ts` are written by hand.

## Credit outside sources

`CREDITS.md` lists the projects SpiceForge depends on and the outside work that contributed insight to its solutions.
Update it whenever an external source is used for tangible benefit:

- **A new dependency:** a submodule, NuGet package or asset (font, icon, image).
- **An idea that went into a solution:** another repository's technique, a pull request or issue, a paper, or a
  branch whose approach (or problems) shaped something here. Link it, name its author where known, and say what it
  contributed.

Check names, titles and links against the source rather than from memory, and mention the source in
`LessonsLearned.md` where the idea is used.

## Working conventions

- **LiveSPICE stays unmodified.** `Dependencies/LiveSPICE` is a pinned submodule; everything SpiceForge replaces
  starts after `Circuit.Analyze()`.
- **The interpreter and the compiled engine agree bit for bit.** A change to one is mirrored in the other, in the same
  order of operations, and checked.
- **Validate before calling something done.** `SpiceForge.Validate` has the checks (`dynamic`, `processor`,
  `switches`, `rejection`, `livespice` and more); run the ones a change touches, and report what they showed.
- **Commit and push only when asked.**
