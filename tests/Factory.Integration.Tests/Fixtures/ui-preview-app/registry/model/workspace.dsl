workspace "Bordereaux" {
  model {
    uw = person "Underwriter"
    app = softwareSystem "Bordereaux app"
    uw -> app "Browses bordereaux"
  }
}
