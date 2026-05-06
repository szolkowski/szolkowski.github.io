source "https://rubygems.org"
# Hello! This is where you manage which Jekyll version is used to run.
# When you want to use a different version, change it below, save the
# file and run `bundle install`. Run Jekyll with `bundle exec`, like so:
#
#     bundle exec jekyll serve
#
# This will help ensure the proper Jekyll version is running.
# Happy Jekylling!
gem "jekyll", "~> 4.3.3"
# Theme is pulled via remote_theme: pages-themes/architect (see _config.yml);
# no `gem "<theme>"` line is needed.
# If you want to use GitHub Pages, remove the "gem "jekyll"" above and
# uncomment the line below. To upgrade, run `bundle update github-pages`.
# gem "github-pages", group: :jekyll_plugins
# If you have any plugins, put them here!
group :jekyll_plugins do
  gem "jekyll-feed"
  gem 'jekyll-sitemap'
  # Pinned: _layouts/default.html captures {% seo %}'s output and strips its
  # auto JSON-LD block via Liquid split. A gem bump that changes template.html
  # formatting could break that split silently. Re-verify the capture/strip
  # logic in default.html before bumping the upper bound.
  gem 'jekyll-seo-tag', '~> 2.8.0'
  gem 'jekyll-remote-theme'
  gem 'jekyll-last-modified-at'
end

# Windows and JRuby does not include zoneinfo files, so bundle the tzinfo-data gem
# and associated library.
platforms :mingw, :x64_mingw, :mswin, :jruby do
  gem "tzinfo", "~> 1.2"
  gem "tzinfo-data"
end

# Performance-booster for watching directories on Windows
gem "wdm", "~> 0.1.1", :platforms => [:mingw, :x64_mingw, :mswin]

# Lock `http_parser.rb` gem to `v0.6.x` on JRuby builds since newer versions of the gem
# do not have a Java counterpart.
gem "http_parser.rb", "~> 0.6.0", :platforms => [:jruby]

gem "webrick", "~> 1.7"

gem 'jekyll-redirect-from'
