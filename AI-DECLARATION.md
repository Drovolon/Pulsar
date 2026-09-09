---
version: "0.1.1"
level: assist
processes:
  design: hint
  implementation: hint
  documentation: none
  review: assist
  deployment: none
---

This format is based on [AI-DECLARATION.md](https://ai-declaration.md/en/0.1.1).

## Legend

Paraphrasing my understanding of ai-declaration.md:

* **none**: no LLM involvement.
* **hint**: human does the all work, uses LLM for passive suggestions/review
* **assist**: human gives specific prompts, LLM does specific, scoped work

## Notes

- **Design** (**hint**): I described the full vision in detail, received feedback (some good, some bad) and iterated on
  that until I was happy with the shape of the design. The final output was a gigantic Markdown file that I would
  sometimes reference during implementation - though the further I got into implementing things, the less useful it was,
  since I made different choices along the way.
- **Implementation** (**hint**)
    * *Most* of Pulsar was written by hand while I was traveling, often with spotty or no internet. Though, I did
      reference the giant Markdown design doc.
    * I delegated *some* tightly scoped, approved-design tasks to an LLM. They're not quite boilerplate but they were
      close. Things like NaturalPathComparer or HostArgs.
- **Documentation** (**none**): All 100% handwritten. No LLM involvement.
- **Review** (**assist**): For code review, passed to an LLM for proofreading and feedback. **If** the feedback
  was easy to implement and mechanical, I would sometimes permit the LLM to make those changes itself. Which I would
  then follow up with my own review.
  - This ended up finding 11 bugs in my code. It felt worthwhile.

## Vibecoded?!

No. The best defense I can give is that I wrote [DESIGN.md](./DESIGN.md) from memory when I was finished with the MVP
implementation. Read it, if you want. Feel free to send me questions.

## Philosophy

Look, I get it. Here's my (US-centric) PoV: we have a *really fucking bad* problem with capital, and it's not just AI.
No company, whether it's Nestle or OpenAI, should be permitted to drain reservoirs for profit or raise the avg temp of
an already sweltering city by multiple degrees (F).

My opinion has changed over time, but my worldview was sort of upended talking with some Asian colleagues. When
I was describing some of my reservations to them, they were just like "well wait, if that's a problem why wouldn't the
government take care of it?" At first, I was just bewildered, but the longer I thought about it, the more I came
to agree: *why are* we in this state? IMO: our problem is *unrestrained capital*.

So yeah, I use LLMs as a tool sometimes, and I try to do my (incredibly small) part in advocating for change
in ways that are more meaningful.
