module Jekyll
  # Generates a tag page (layout: tagpage, permalink: /tags/<slug>) for every
  # unique tag found across site.posts. Hand-authored intros + meta descriptions
  # for prominent tags live in _data/tag_descriptions.yml, keyed by slug; tags
  # without an entry get a generic stub.
  #
  # Replaces the old standalone _gentags.rb script — runs on every jekyll build,
  # so tag pages cannot drift out of sync with post frontmatter.
  class TagPageGenerator < Generator
    safe true
    priority :normal

    GENERIC_DESCRIPTION = "Posts on Szołkowski's Blog tagged %s — Optimizely CMS, .NET, and developer-experience writing.".freeze

    def generate(site)
      descriptions = site.data['tag_descriptions'] || {}

      tags = site.posts.docs
                  .flat_map { |post| Array(post.data['tags']) }
                  .map { |t| t.to_s.strip }
                  .reject(&:empty?)
                  .uniq { |t| t.downcase }

      tags.each do |tag|
        site.pages << build_tag_page(site, tag, descriptions)
      end
    end

    private

    def build_tag_page(site, tag, descriptions)
      slug = Jekyll::Utils.slugify(tag)
      entry = descriptions[slug] || {}
      desc = entry['description'] || (GENERIC_DESCRIPTION % tag)

      page = PageWithoutAFile.new(site, site.source, 'tags', "#{slug}.html")
      page.data.merge!(
        'layout' => 'tagpage',
        'tag' => tag,
        'title' => "Posts tagged #{tag}",
        'description' => desc,
        'permalink' => "/tags/#{slug}",
        # Set explicitly so jekyll-last-modified-at doesn't try to git-mtime a
        # file that exists only in memory. Use the most-recent post date for
        # this tag so dateModified actually means something.
        'last_modified_at' => latest_post_date_for(site, slug)
      )
      page.data['intro'] = entry['intro'] if entry['intro']
      page.content = ''
      page
    end

    def latest_post_date_for(site, slug)
      matching = site.posts.docs.select do |post|
        Array(post.data['tags']).any? { |t| Jekyll::Utils.slugify(t.to_s) == slug }
      end
      return site.time if matching.empty?
      matching.map(&:date).max
    end
  end
end
