---
version: "0.1.1"
level: assist
processes:
  design: hint
  implementation: assist
  testing: copilot
  documentation: hint
  review: assist
  deployment: none
components:
  Pulsar.Tests: copilot
---

This format is based on [AI-DECLARATION.md](https://ai-declaration.md/en/0.1.1).

## Legend

Paraphrasing my understanding of ai-declaration.md:

* **hint**: human does the all work, uses LLM for passive suggestions
* **assist**: human gives specific prompts, LLM does specific, scoped work
* **copilot**: human provides a prompt and steps back, lets the LLM do most of the work

## Notes

- **Design** (**hint**): I used an LLM like a rubber duck: basically described the full vision in detail, received feedback (some good, some bad) and iterated on that until I was happy with the shape of the design. The final output was a gigantic Markdown file that I would sometimes reference during implementation - though the further I got into implementing things, the less useful it was, since I made different choices along the way.
- **Implementation** (somewhere **between hint and assist**)
    * *Most* of Pulsar was written by hand while I was traveling, often with spotty or no internet. Though, I did reference the giant Markdown design doc.
    * I delegated *some* tightly scoped, approved-design tasks to an LLM. They're not quite boilerplate but they were close. Things like NaturalPathComparer or HostArgs.
- **Testing** (**copilot**): **Unit tests (Pulsar.Tests) are entirely LLM generated**, albeit with guidance. No way I'd ever write tests this comprehensive for a hobby project.
- **Documentation** (**hint**): All 100% handwritten, but passed to an LLM for proofreading and review. LLM made no edits; any suggestion I reviewed and applied (or not).
- **Review** (**assist**): For code review, again, passed to an LLM for proofreading and feedback. **If** the feedback was easy to implement and mechanical, I would sometimes permit the LLM to make those changes itself. Which I would then follow up with my own review.
