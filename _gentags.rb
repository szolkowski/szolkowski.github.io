require 'yaml'
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

tags.uniq { |t| t.downcase }.each do |tag|
	slug = tag.downcase.gsub(/^\./, '').gsub(/[^a-z0-9\-]/, '-').gsub(/-+/, '-').gsub(/^-|-$/, '')
	File.write File.join('tags', "#{slug}.html"), <<-EOF
---
layout: tagpage
tag: #{tag}
permalink: /tags/#{slug}
---
	EOF
end