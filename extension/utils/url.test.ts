import { describe, expect, it } from "vitest";
import {
  canonicalizePostUrl,
  isTrackingParam,
} from "./url";

describe("isTrackingParam", () => {
  it("flags known LinkedIn tracking params", () => {
    expect(isTrackingParam("li_ucat")).toBe(true);
    expect(isTrackingParam("li_ucat.joyce")).toBe(true);
    expect(isTrackingParam("trackingId")).toBe(true);
    expect(isTrackingParam("trk")).toBe(true);
    expect(isTrackingParam("trkInfo")).toBe(true);
    expect(isTrackingParam("origin")).toBe(true);
    expect(isTrackingParam("utm_source")).toBe(true);
    expect(isTrackingParam("lipi")).toBe(true);
    expect(isTrackingParam("eid")).toBe(true);
  });

  it("keeps content params", () => {
    expect(isTrackingParam("comment_id")).toBe(false);
    expect(isTrackingParam("replyUrn")).toBe(false);
  });
});

describe("canonicalizePostUrl", () => {
  it("strips tracking params and keeps path", () => {
    expect(
      canonicalizePostUrl(
        "https://www.linkedin.com/feed/update/urn:li:activity:123?li_ucat=ab&trackingId=x&origin=SAVED",
      ),
    ).toBe("https://www.linkedin.com/feed/update/urn:li:activity:123");
  });

  it("keeps legitimate query params (comment_id, replyUrn)", () => {
    expect(
      canonicalizePostUrl(
        "https://www.linkedin.com/feed/update/urn:li:activity:123?commentUrn=urn%3Ali%3Acomment%3A9&replyUrn=urn%3Ali%3Acomment%3A10",
      ),
    ).toBe(
      "https://www.linkedin.com/feed/update/urn:li:activity:123?commentUrn=urn:li:comment:9&replyUrn=urn:li:comment:10",
    );
  });

  it("resolves relative /feed/update paths", () => {
    expect(
      canonicalizePostUrl("/feed/update/urn:li:ugcPost:99?trk=public_post"),
    ).toBe("https://www.linkedin.com/feed/update/urn:li:ugcPost:99");
  });

  it("resolves protocol-relative URLs", () => {
    expect(canonicalizePostUrl("//www.linkedin.com/posts/x")).toBe(
      "https://www.linkedin.com/posts/x",
    );
  });

  it("strips the fragment", () => {
    expect(canonicalizePostUrl("https://www.linkedin.com/posts/x#foo")).toBe(
      "https://www.linkedin.com/posts/x",
    );
  });

  it("returns null on garbage input", () => {
    expect(canonicalizePostUrl(null)).toBeNull();
    expect(canonicalizePostUrl("")).toBeNull();
    expect(canonicalizePostUrl("not a url")).toBeNull();
    expect(canonicalizePostUrl("javascript:alert(1)")).toBeNull();
  });
});
