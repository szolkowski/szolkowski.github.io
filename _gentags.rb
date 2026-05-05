require 'yaml'
require 'json'

tags = []
Dir.glob(File.join('_posts','*.md')).each do |file|
	yaml_s = File.read(file).split(/^---$/)[1]
	yaml_h = YAML.safe_load(yaml_s, permitted_classes: [Time])
	tags += yaml_h['tags'] if yaml_h['tags']
end

Dir.glob(File.join('_posts','*.markdown')).each do |file|
	yaml_s = File.read(file).split(/^---$/)[1]
	yaml_h = YAML.safe_load(yaml_s, permitted_classes: [Time])
	tags += yaml_h['tags'] if yaml_h['tags']
end

descriptions = {}
desc_path = File.join('_data', 'tag_descriptions.yml')
if File.exist?(desc_path)
	descriptions = YAML.safe_load(File.read(desc_path)) || {}
end

generic_desc = lambda do |tag|
	"Posts on Szołkowski's Blog tagged #{tag} — Optimizely CMS, .NET, and developer-experience writing."
end

tags.uniq { |t| t.downcase }.each do |tag|
	slug = tag.downcase.gsub(/^\./, '').gsub(/[^a-z0-9\-]/, '-').gsub(/-+/, '-').gsub(/^-|-$/, '')
	entry = descriptions[slug] || {}
	desc = entry['description'] || generic_desc.call(tag)
	intro = entry['intro']

	frontmatter = [
		'---',
		'layout: tagpage',
		"tag: #{tag.to_json}",
		"title: \"Posts tagged #{tag}\"",
		"description: #{desc.to_json}",
		"permalink: /tags/#{slug}"
	]
	frontmatter << "intro: #{intro.to_json}" if intro
	frontmatter << '---'

	File.write File.join('tags', "#{slug}.html"), frontmatter.join("\n") + "\n"
end
